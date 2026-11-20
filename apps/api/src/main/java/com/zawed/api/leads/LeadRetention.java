package com.zawed.api.leads;

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
 * Deletes closed leads after their retention (u1-leads-contract.md §6): {@code zawed.leads.closed-retention},
 * 24 months after closing by default. Open leads are kept: they are Operations' work in progress. The
 * deletion is {@code purge_closed_leads} (V22), a bounded {@code SECURITY DEFINER} function that refuses
 * a cutoff inside {@link #DATABASE_FLOOR}; the runtime role has no DELETE on {@code leads}. In bounded
 * batches, each its own transaction, like {@code IdempotencyKeyRetention}.
 */
@Component
class LeadRetention {

	private static final Logger LOG = LoggerFactory.getLogger(LeadRetention.class);

	/** What {@code purge_closed_leads} refuses to go under, whoever calls it. */
	static final Duration DATABASE_FLOOR = Duration.ofDays(90);

	/** The shortest retention the job asks for: a day over the floor, for a clock ahead of the database's. */
	static final Duration MINIMUM = DATABASE_FLOOR.plusDays(1);

	static final int BATCH = 1_000;

	static final int MAX_BATCHES_PER_RUN = 20;

	private final JdbcClient jdbc;
	private final TransactionTemplate transactions;
	private final Clock clock;
	private final Duration retention;

	LeadRetention(JdbcClient jdbc, TransactionTemplate transactions, Clock clock,
			@Value("${zawed.leads.closed-retention:P730D}") Duration retention) {
		this.jdbc = jdbc;
		this.transactions = transactions;
		this.clock = clock;
		this.retention = effective(retention);
	}

	static Duration effective(Duration configured) {
		return configured.compareTo(MINIMUM) < 0 ? MINIMUM : configured;
	}

	@Scheduled(initialDelayString = "PT10M", fixedDelayString = "${zawed.leads.purge-interval:PT24H}")
	void scheduled() {
		purge();
	}

	/** @return how many closed leads were deleted */
	long purge() {
		Instant before = clock.instant().minus(retention);
		long total = 0;
		boolean finished = false;
		for (int batch = 0; batch < MAX_BATCHES_PER_RUN && !finished; batch++) {
			Integer deleted = transactions.execute((status) -> jdbc.sql("select purge_closed_leads(:before, :limit)")
				.param("before", Sql.ts(before))
				.param("limit", BATCH)
				.query(Integer.class)
				.single());
			total += deleted == null ? 0 : deleted;
			finished = deleted == null || deleted < BATCH;
		}
		if (total > 0) {
			LOG.info("Purged {} leads closed before {}", total, before);
		}
		return total;
	}

}
