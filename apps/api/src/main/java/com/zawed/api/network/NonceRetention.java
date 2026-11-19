package com.zawed.api.network;

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

import com.zawed.api.platform.persistence.Sql;

/**
 * Keeps {@code device_nonces} bounded (audit F5, V14). A device signs a fresh nonce for every token,
 * at most every 15 minutes while it is in use: a terminal in use around the clock writes up to 96 a
 * day, so 1,000 such terminals write up to about 35 M a year if nothing is removed.
 *
 * <p>A token request is accepted only within ±5 minutes of its own timestamp, so a nonce can be
 * replayed for at most 10 minutes; nonces older than {@code zawed.pos.nonce-retention} (one hour by
 * default, never under 15 minutes, which the database function enforces too) are deleted in bounded
 * batches, each its own short transaction, so the job never holds many row locks or one long
 * transaction. Two instances running it at once only share the work.
 */
@Component
class NonceRetention {

	private static final Logger LOG = LoggerFactory.getLogger(NonceRetention.class);

	static final int BATCH = 5_000;

	/** A run stops after this many batches and finishes on the next run. */
	static final int MAX_BATCHES_PER_RUN = 40;

	private final JdbcClient jdbc;
	private final TransactionTemplate transactions;
	private final Clock clock;
	private final Duration retention;

	NonceRetention(JdbcClient jdbc, TransactionTemplate transactions, Clock clock,
			@Value("${zawed.pos.nonce-retention:PT1H}") Duration retention) {
		this.jdbc = jdbc;
		this.transactions = transactions;
		this.clock = clock;
		this.retention = retention.compareTo(Duration.ofMinutes(15)) < 0 ? Duration.ofMinutes(15) : retention;
	}

	@Scheduled(initialDelayString = "PT2M", fixedDelayString = "${zawed.pos.nonce-purge-interval:PT15M}")
	void scheduled() {
		purge();
	}

	/** @return how many nonces were deleted */
	long purge() {
		Instant before = clock.instant().minus(retention);
		long total = 0;
		for (int batch = 0; batch < MAX_BATCHES_PER_RUN; batch++) {
			Integer deleted = transactions.execute((status) -> jdbc.sql("select purge_device_nonces(:before, :limit)")
				.param("before", Sql.ts(before))
				.param("limit", BATCH)
				.query(Integer.class)
				.single());
			total += deleted == null ? 0 : deleted;
			if (deleted == null || deleted < BATCH) {
				break;
			}
		}
		if (total > 0) {
			LOG.info("Purged {} device nonces older than {}", total, before);
		}
		return total;
	}

}
