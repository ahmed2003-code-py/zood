package com.zawed.api.platform.ratelimit;

import java.time.Clock;
import java.time.Duration;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;

import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Component;

/**
 * Fixed-window counters in memory. Keys are hashed by the caller when they contain personal data.
 * Expired windows are swept every minute so the map cannot grow without bound.
 */
@Component
public class InMemoryRateLimiter implements RateLimiter {

	private record Window(long startMillis, long lengthMillis, AtomicInteger count) {
	}

	private final Clock clock;
	private final ConcurrentHashMap<String, Window> windows = new ConcurrentHashMap<>();

	InMemoryRateLimiter(Clock clock) {
		this.clock = clock;
	}

	@Override
	public void acquire(String key, int limit, Duration window) {
		long now = clock.millis();
		long length = window.toMillis();
		Window current = windows.compute(key, (ignored, existing) -> existing == null
				|| now - existing.startMillis() >= existing.lengthMillis() ? new Window(now, length, new AtomicInteger())
						: existing);
		if (current.count().incrementAndGet() > limit) {
			long remaining = current.startMillis() + current.lengthMillis() - now;
			throw new RateLimitedException(Math.max(1, (remaining + 999) / 1000));
		}
	}

	@Scheduled(fixedDelay = 60_000)
	void sweep() {
		long now = clock.millis();
		windows.values().removeIf((window) -> now - window.startMillis() >= window.lengthMillis());
	}

	/** Tests only: forget every counter. */
	public void reset() {
		windows.clear();
	}

}
