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

/**
 * Keeps {@code idempotency_keys} bounded (V17, ADR 0022). A key only has to outlive every retry of
 * the request it was sent with: the web client makes one per open form and resends it within seconds
 * or minutes, and even a form left open over a long weekend is days. Keys older than
 * {@code zawed.idempotency.key-retention} (30 days by default, never under {@link #MINIMUM}) are
 * deleted in bounded batches, each its own short transaction. Two instances running it at once only
 * share the work.
 *
 * <p>After the purge, a request sent again with a purged key is a new request: that is what the
 * retention means, and why it is far longer than any retry.
 *
 * <p>The deletion is {@code purge_idempotency_keys}, a bounded {@code SECURITY DEFINER} function that
 * refuses a cutoff inside {@link #DATABASE_FLOOR}, like V14's {@code purge_device_nonces}.
 * The runtime role has no DELETE on the table and the job needs no scope: no session of the
 * application, a POS route included, can read or delete another user's key.
 */
@Component
class IdempotencyKeyRetention {

	private static final Logger LOG = LoggerFactory.getLogger(IdempotencyKeyRetention.class);

	/** What {@code purge_idempotency_keys} refuses to go under, whoever calls it. */
	static final Duration DATABASE_FLOOR = Duration.ofDays(7);

	/**
	 * The shortest retention the job asks for: the database floor plus a day, so that a clock ahead of
	 * the database's never makes the function refuse the job's own cutoff.
	 */
	static final Duration MINIMUM = DATABASE_FLOOR.plusDays(1);

	static final int BATCH = 5_000;

	/** A run stops after this many batches and finishes on the next run. */
	static final int MAX_BATCHES_PER_RUN = 40;

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
		return configured.compareTo(MINIMUM) < 0 ? MINIMUM : configured;
	}

	@Scheduled(initialDelayString = "PT5M", fixedDelayString = "${zawed.idempotency.purge-interval:PT6H}")
	void scheduled() {
		purge();
	}

	/** @return how many keys were deleted */
	long purge() {
		Instant before = clock.instant().minus(retention);
		long total = 0;
		boolean finished = false;
		for (int batch = 0; batch < MAX_BATCHES_PER_RUN && !finished; batch++) {
			Integer deleted = transactions.execute((status) -> jdbc.sql("select purge_idempotency_keys(:before, :limit)")
				.param("before", Sql.ts(before))
				.param("limit", BATCH)
				.query(Integer.class)
				.single());
			total += deleted == null ? 0 : deleted;
			finished = deleted == null || deleted < BATCH;
		}
		if (!finished) {
			LOG.warn("Idempotency key purge stopped after {} batches; the rest is deleted on the next run",
					MAX_BATCHES_PER_RUN);
		}
		if (total > 0) {
			LOG.info("Purged {} idempotency keys older than {}", total, before);
		}
		return total;
	}

}
