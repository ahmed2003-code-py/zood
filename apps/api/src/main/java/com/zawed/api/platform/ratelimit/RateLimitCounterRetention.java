package com.zawed.api.platform.ratelimit;

import java.time.Duration;
import java.util.Map;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Component;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.platform.jobs.Job;
import com.zawed.api.platform.jobs.JobResult;
import com.zawed.api.platform.jobs.Jobs;

/**
 * Keeps {@code rate_limit_counters} bounded (V22, ADR 0012 amendment): windows that have ended are
 * deleted every hour, in bounded batches, each its own transaction, through
 * {@code purge_rate_limit_counters}, which removes nothing that is still running. Two instances running
 * it at once only share the work.
 */
@Component
class RateLimitCounterRetention {

	private static final Logger LOG = LoggerFactory.getLogger(RateLimitCounterRetention.class);

	static final int BATCH = 5_000;

	static final int MAX_BATCHES_PER_RUN = 40;

	private final JdbcClient jdbc;
	private final TransactionTemplate transactions;
	private final Jobs jobs;
	private final Job job;

	RateLimitCounterRetention(JdbcClient jdbc, TransactionTemplate transactions, Jobs jobs) {
		this.jdbc = jdbc;
		this.transactions = transactions;
		this.jobs = jobs;
		// Hourly: three missed runs before the health check says DOWN (ADR 0028).
		this.job = jobs.define("rate-limit-retention", Duration.ofHours(3));
	}

	@Scheduled(initialDelayString = "PT5M", fixedDelayString = "${zawed.rate-limit.purge-interval:PT1H}")
	void scheduled() {
		jobs.run(job, () -> JobResult.success(Map.of("deleted", purge())));
	}

	/** @return how many ended windows were deleted */
	long purge() {
		long total = 0;
		boolean finished = false;
		for (int batch = 0; batch < MAX_BATCHES_PER_RUN && !finished; batch++) {
			Integer deleted = transactions.execute((status) -> jdbc.sql("select purge_rate_limit_counters(:limit)")
				.param("limit", BATCH)
				.query(Integer.class)
				.single());
			total += deleted == null ? 0 : deleted;
			finished = deleted == null || deleted < BATCH;
		}
		if (total > 0) {
			LOG.info("Purged {} expired rate-limit windows", total);
		}
		return total;
	}

}
