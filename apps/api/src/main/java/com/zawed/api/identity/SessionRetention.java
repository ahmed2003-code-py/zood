package com.zawed.api.identity;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.Map;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Component;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.platform.jobs.Job;
import com.zawed.api.platform.jobs.JobResult;
import com.zawed.api.platform.jobs.Jobs;
import com.zawed.api.platform.persistence.Sql;

/**
 * Keeps the web sign-in tables bounded (V21, ADR 0028).
 *
 * <ul>
 * <li><b>Sessions</b> are deleted {@code zawed.sessions.retention} (90 days by default, never under
 * {@link #SESSION_MINIMUM}) after they <em>ended</em>: revoked, idle-expired or past their absolute
 * end, whichever came first. That keeps a session's device, address prefix and end reason for an
 * investigation well after the fact, while the audit log keeps every sign-in, sign-out and revocation
 * for ever. A live session ends in the future, so no cutoff can reach it.</li>
 * <li><b>MFA challenges</b> are deleted {@code zawed.sessions.mfa-challenge-retention} (one day by
 * default, never under {@link #CHALLENGE_MINIMUM}) after they expired. A challenge lives at most ten
 * minutes; an unfinished enrolment's row still holds the pending TOTP secret, so it should not linger.</li>
 * </ul>
 *
 * <p>Both go through bounded {@code SECURITY DEFINER} functions ({@code purge_sessions},
 * {@code purge_mfa_challenges}) that refuse a cutoff inside their own floor, like
 * {@code purge_idempotency_keys}: the runtime role has no DELETE on either table. Each batch is its
 * own short transaction, and rows are taken with {@code SKIP LOCKED}, so a purge never waits behind a
 * sign-in or a refresh. Two instances running it at once only share the work.
 *
 * <p>Station agents' sessions ({@code agent_sessions}) are a different table and are never purged:
 * shifts and the "last shift" period are built from them.
 */
@Component
class SessionRetention {

	private static final Logger LOG = LoggerFactory.getLogger(SessionRetention.class);

	/** What {@code purge_sessions} refuses to go under, whoever calls it. */
	static final Duration SESSION_FLOOR = Duration.ofDays(30);

	/** The floor plus a day, so a clock ahead of the database's never trips the function's floor. */
	static final Duration SESSION_MINIMUM = SESSION_FLOOR.plusDays(1);

	/** What {@code purge_mfa_challenges} refuses to go under. */
	static final Duration CHALLENGE_FLOOR = Duration.ofHours(1);

	static final Duration CHALLENGE_MINIMUM = CHALLENGE_FLOOR.plusHours(1);

	static final int BATCH = 5_000;

	/** A run stops after this many batches per table and finishes on the next run. */
	static final int MAX_BATCHES_PER_RUN = 40;

	private final JdbcClient jdbc;
	private final TransactionTemplate transactions;
	private final Clock clock;
	private final Duration sessionRetention;
	private final Duration challengeRetention;
	private final Jobs jobs;
	private final Job job;

	SessionRetention(JdbcClient jdbc, TransactionTemplate transactions, Clock clock, Jobs jobs,
			@Value("${zawed.sessions.retention:P90D}") Duration sessionRetention,
			@Value("${zawed.sessions.mfa-challenge-retention:P1D}") Duration challengeRetention) {
		this.jdbc = jdbc;
		this.transactions = transactions;
		this.clock = clock;
		this.sessionRetention = atLeast(sessionRetention, SESSION_MINIMUM);
		this.challengeRetention = atLeast(challengeRetention, CHALLENGE_MINIMUM);
		this.jobs = jobs;
		this.job = jobs.define("session-retention", Duration.ofHours(4));
	}

	static Duration atLeast(Duration configured, Duration minimum) {
		return configured.compareTo(minimum) < 0 ? minimum : configured;
	}

	@Scheduled(initialDelayString = "PT7M", fixedDelayString = "${zawed.sessions.purge-interval:PT1H}")
	void scheduled() {
		jobs.run(job, () -> {
			long sessions = purgeSessions();
			long challenges = purgeChallenges();
			return JobResult.success(Map.of("sessions", sessions, "mfaChallenges", challenges));
		});
	}

	/** @return how many ended sessions were deleted */
	long purgeSessions() {
		Instant before = clock.instant().minus(sessionRetention);
		long total = purge("purge_sessions", before);
		if (total > 0) {
			LOG.info("Purged {} sessions that ended before {}", total, before);
		}
		return total;
	}

	/** @return how many expired MFA challenges were deleted */
	long purgeChallenges() {
		Instant before = clock.instant().minus(challengeRetention);
		long total = purge("purge_mfa_challenges", before);
		if (total > 0) {
			LOG.info("Purged {} MFA challenges that expired before {}", total, before);
		}
		return total;
	}

	private long purge(String function, Instant before) {
		long total = 0;
		boolean finished = false;
		for (int batch = 0; batch < MAX_BATCHES_PER_RUN && !finished; batch++) {
			Integer deleted = transactions.execute((status) -> jdbc.sql("select " + function + "(:before, :limit)")
				.param("before", Sql.ts(before))
				.param("limit", BATCH)
				.query(Integer.class)
				.single());
			total += deleted == null ? 0 : deleted;
			finished = deleted == null || deleted < BATCH;
		}
		if (!finished) {
			LOG.warn("{} stopped after {} batches; the rest is deleted on the next run", function, MAX_BATCHES_PER_RUN);
		}
		return total;
	}

}
