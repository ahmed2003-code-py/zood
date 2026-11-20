package com.zawed.api.platform.ratelimit;

import java.time.Duration;

/**
 * Counts attempts per key in fixed windows. The in-memory implementation is correct for one
 * instance and resets on every deploy. Where a limit must survive a deploy and hold across
 * instances, {@link PostgresRateLimiter} counts in the database instead (ADR 0012, amended for U1).
 */
public interface RateLimiter {

	/**
	 * Counts one attempt for {@code key}. Throws {@link RateLimitedException} when more than
	 * {@code limit} attempts fall in the current window.
	 */
	void acquire(String key, int limit, Duration window);

}
