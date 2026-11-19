package com.zawed.api.platform.persistence;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Component;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Scope;

/**
 * Keeps {@code idempotency_keys} bounded (V17, ADR 0022). A key only has to outlive every retry of
 * the request it was sent with: the web client makes one per open form and resends it within seconds
 * or minutes, and even a form left open over a long weekend is days. Keys older than
 * {@code zawed.idempotency.key-retention} (30 days by default, never under {@link #FLOOR}, which the
 * database enforces too) are deleted in bounded batches, each its own short transaction. Two
 * instances running it at once only share the work.
 *
 * <p>After the purge, a request sent again with a purged key is a new request: that is what the
 * retention means, and why it is far longer than any retry.
 *
 * <p>Scope: the job has no request and no user. It runs in the platform scope with no user
 * ({@code DbScope.runAs}, a reviewed entry in {@code ArchitectureTests}), which is the only session
 * V17's row-level security lets delete a key, and only one older than the floor.
 */
@Component
class IdempotencyKeyRetention {

	private static final Logger LOG = LoggerFactory.getLogger(IdempotencyKeyRetention.class);

	/** The shortest retention; V17's delete policy refuses anything younger, whatever is configured. */
	static final Duration FLOOR = Duration.ofDays(7);

	static final int BATCH = 5_000;

	/** A run stops after this many batches and finishes on the next run. */
	static final int MAX_BATCHES_PER_RUN = 40;

	private static final DbScope.Context PURGE_JOB = new DbScope.Context(Scope.PLATFORM, null);

	private final JdbcClient jdbc;
	private final TransactionTemplate transactions;
	private final Clock clock;
	private final Duration retention;

	IdempotencyKeyRetention(JdbcClient jdbc, TransactionTemplate transactions, Clock clock,
			@Value("${zawed.idempotency.key-retention:P30D}") Duration retention) {
		this.jdbc = jdbc;
		this.transactions = transactions;
		this.clock = clock;
		this.retention = effective(retention);
	}

	static Duration effective(Duration configured) {
		return configured.compareTo(FLOOR) < 0 ? FLOOR : configured;
	}

	@Scheduled(initialDelayString = "PT5M", fixedDelayString = "${zawed.idempotency.purge-interval:PT6H}")
	void scheduled() {
		purge();
	}

	/** @return how many keys were deleted */
	long purge() {
		Instant before = clock.instant().minus(retention);
		long total = DbScope.runAs(PURGE_JOB, () -> {
			long deletedInRun = 0;
			for (int batch = 0; batch < MAX_BATCHES_PER_RUN; batch++) {
				Integer deleted = transactions.execute((status) -> jdbc.sql("""
						delete from idempotency_keys k
						using (select actor_id, operation, key from idempotency_keys
						       where created_at < :before order by created_at limit :limit) old
						where k.actor_id = old.actor_id and k.operation = old.operation and k.key = old.key
						""").param("before", Sql.ts(before)).param("limit", BATCH).update());
				deletedInRun += deleted == null ? 0 : deleted;
				if (deleted == null || deleted < BATCH) {
					return deletedInRun;
				}
			}
			LOG.warn("Idempotency key purge stopped after {} batches; the rest is deleted on the next run",
					MAX_BATCHES_PER_RUN);
			return deletedInRun;
		});
		if (total > 0) {
			LOG.info("Purged {} idempotency keys older than {}", total, before);
		}
		return total;
	}

}
