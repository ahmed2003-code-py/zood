package com.zawed.api.platform.jobs;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.Map;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Component;
import org.springframework.transaction.support.TransactionTemplate;

/**
 * Keeps {@code job_runs} itself bounded (V21, ADR 0028): runs that started more than
 * {@code zawed.jobs.run-retention} ago (90 days by default; never under {@link #MINIMUM}) are deleted
 * through {@code purge_job_runs}, which refuses anything inside {@link #DATABASE_FLOOR}. About 280
 * runs a day per instance are recorded, so the table stays near 25,000 rows. It is a job like the others, and so
 * is recorded too.
 */
@Component
class JobRunRetention {

	private static final Logger LOG = LoggerFactory.getLogger(JobRunRetention.class);

	static final Duration DATABASE_FLOOR = Duration.ofDays(30);

	/** The floor plus a day, so a clock ahead of the database's never trips the function's floor. */
	static final Duration MINIMUM = DATABASE_FLOOR.plusDays(1);

	static final int BATCH = 5_000;

	static final int MAX_BATCHES_PER_RUN = 20;

	private final JobRunStore store;
	private final TransactionTemplate tx;
	private final Clock clock;
	private final Duration retention;
	private final Jobs jobs;
	private final Job job;

	JobRunRetention(JobRunStore store, TransactionTemplate tx, Clock clock, Jobs jobs,
			@Value("${zawed.jobs.run-retention:P90D}") Duration retention) {
		this.store = store;
		this.tx = tx;
		this.clock = clock;
		this.retention = retention.compareTo(MINIMUM) < 0 ? MINIMUM : retention;
		this.jobs = jobs;
		this.job = jobs.define("job-run-retention", Duration.ofHours(26));
	}

	@Scheduled(initialDelayString = "PT10M", fixedDelayString = "${zawed.jobs.run-purge-interval:PT6H}")
	void scheduled() {
		jobs.run(job, () -> JobResult.success(Map.of("deleted", purge())));
	}

	/** @return how many runs were deleted */
	long purge() {
		Instant before = clock.instant().minus(retention);
		long total = 0;
		for (int batch = 0; batch < MAX_BATCHES_PER_RUN; batch++) {
			Integer deleted = tx.execute((status) -> store.purge(before, BATCH));
			total += deleted == null ? 0 : deleted;
			if (deleted == null || deleted < BATCH) {
				break;
			}
		}
		if (total > 0) {
			LOG.info("Purged {} job runs older than {}", total, before);
		}
		return total;
	}

}
