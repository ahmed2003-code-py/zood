package com.zawed.api.notifications;

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
 * Keeps {@code notifications} bounded (B7 W5): notifications older than
 * {@code zawed.notifications.retention} (90 days by default, never under 30, which the database
 * function enforces too) are deleted with their read rows, in bounded batches, each its own short
 * transaction. Modelled on {@code network.NonceRetention}.
 *
 * <p>The delete goes through {@code purge_notifications}, a narrow SECURITY DEFINER function, rather
 * than a platform-scope escalation ({@code DbScope.runAs}): {@code zawed_app} then holds no
 * {@code DELETE} on either table at all, so no request in any scope can remove a notification, and
 * the closed list of escalations in {@code ArchitectureTests} stays as it is.
 */
@Component
class NotificationRetention {

	private static final Logger LOG = LoggerFactory.getLogger(NotificationRetention.class);

	static final int BATCH = 5_000;

	/** A run stops after this many batches and finishes on the next run. */
	static final int MAX_BATCHES_PER_RUN = 40;

	static final Duration MINIMUM = Duration.ofDays(30);

	private final JdbcClient jdbc;
	private final TransactionTemplate transactions;
	private final Clock clock;
	private final Duration retention;

	NotificationRetention(JdbcClient jdbc, TransactionTemplate transactions, Clock clock,
			@Value("${zawed.notifications.retention:P90D}") Duration retention) {
		this.jdbc = jdbc;
		this.transactions = transactions;
		this.clock = clock;
		this.retention = retention.compareTo(MINIMUM) < 0 ? MINIMUM : retention;
	}

	@Scheduled(initialDelayString = "PT10M", fixedDelayString = "${zawed.notifications.purge-interval:PT6H}")
	void scheduled() {
		purge();
	}

	/** @return how many notifications were deleted */
	long purge() {
		Instant before = clock.instant().minus(retention);
		long total = 0;
		for (int batch = 0; batch < MAX_BATCHES_PER_RUN; batch++) {
			Integer deleted = transactions.execute((status) -> jdbc.sql("select purge_notifications(:before, :limit)")
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
			LOG.info("Purged {} notifications older than {}", total, before);
		}
		return total;
	}

}
