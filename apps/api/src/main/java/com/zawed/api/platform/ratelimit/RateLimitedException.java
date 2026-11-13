package com.zawed.api.platform.ratelimit;

import java.util.Map;

import org.springframework.http.HttpStatus;

import com.zawed.api.platform.web.ApiException;

/** 429 with how long to wait; answered with a {@code Retry-After} header and {@code retryAfterSeconds}. */
public class RateLimitedException extends ApiException {

	private final long retryAfterSeconds;

	public RateLimitedException(long retryAfterSeconds) {
		super(HttpStatus.TOO_MANY_REQUESTS, "RATE_LIMITED", "Too many requests",
				null, Map.of("retryAfterSeconds", retryAfterSeconds));
		this.retryAfterSeconds = retryAfterSeconds;
	}

	public long retryAfterSeconds() {
		return retryAfterSeconds;
	}

}
