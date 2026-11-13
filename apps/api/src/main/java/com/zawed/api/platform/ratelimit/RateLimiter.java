package com.zawed.api.platform.ratelimit;

import java.time.Duration;

/**
 * Counts attempts per key in fixed windows. The in-memory implementation is correct for one
 * instance; with several instances, a Redis implementation of this interface replaces it (ADR 0012).
 */
public interface RateLimiter {

	/**
	 * Counts one attempt for {@code key}. Throws {@link RateLimitedException} when more than
	 * {@code limit} attempts fall in the current window.
	 */
	void acquire(String key, int limit, Duration window);

}
