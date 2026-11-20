package com.zawed.api.platform.ratelimit;

import java.time.Duration;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Component;
import org.springframework.transaction.PlatformTransactionManager;
import org.springframework.transaction.TransactionDefinition;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.platform.crypto.LookupHasher;

/**
 * Fixed-window counters in PostgreSQL (V22, ADR 0012 amendment): a limit that must survive a deploy
 * and hold across instances, which {@link InMemoryRateLimiter} cannot (it resets on every start and
 * counts per instance). Used where an anonymous caller could otherwise wait for a deploy, or spread
 * over instances, to get a fresh budget: the public demo-request form.
 *
 * <p>Windows are aligned to the epoch by the database clock, so every instance agrees where one
 * starts. Each attempt is one upsert in {@code rate_limit_hit}, a {@code SECURITY DEFINER} function:
 * the runtime role has no grant on the table and cannot read, reset or delete a counter. Ended windows are
 * deleted by {@link RateLimitCounterRetention}. The subject
 * (an address, a phone, an e-mail) is stored only as a keyed hash, per bucket.
 *
 * <p>Every attempt counts, refused ones included, in its own short transaction: an attempt that later
 * fails validation, or whose request rolls back, still spends the budget.
 */
@Component
public class PostgresRateLimiter {

	private final JdbcClient jdbc;
	private final TransactionTemplate transactions;
	private final LookupHasher hasher;

	PostgresRateLimiter(JdbcClient jdbc, PlatformTransactionManager transactionManager, LookupHasher hasher) {
		this.jdbc = jdbc;
		this.transactions = new TransactionTemplate(transactionManager);
		// Its own transaction even when a caller already has one: the count must commit either way.
		this.transactions.setPropagationBehavior(TransactionDefinition.PROPAGATION_REQUIRES_NEW);
		this.hasher = hasher;
	}

	/**
	 * Counts one attempt by {@code subject} in {@code bucket}. Throws {@link RateLimitedException}, with
	 * the seconds until the window ends, when more than {@code limit} attempts fall in the window.
	 *
	 * @param bucket what is limited and over which window, e.g. {@code demo-ip-hour}; a constant, never input
	 */
	public void acquire(String bucket, String subject, int limit, Duration window) {
		String keyHash = hasher.hash("rate-limit:" + bucket, subject);
		Hit hit = transactions.execute((status) -> jdbc
			.sql("select hit_count, retry_after_seconds from rate_limit_hit(:bucket, :key, :seconds)")
			.param("bucket", bucket)
			.param("key", keyHash)
			.param("seconds", Math.toIntExact(window.toSeconds()))
			.query((rs, row) -> new Hit(rs.getInt(1), rs.getInt(2)))
			.single());
		if (hit.count() > limit) {
			throw new RateLimitedException(hit.retryAfterSeconds());
		}
	}

	private record Hit(int count, int retryAfterSeconds) {
	}

}
