package com.zawed.api.platform.time;

import java.time.Clock;

import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

/**
 * The one clock. Code asks this bean for the time instead of calling {@code Instant.now()}, so
 * tests can fix or move time (ADR 7). Timestamps are UTC; a station's time zone is applied only
 * when a rule is evaluated or a value is displayed.
 */
@Configuration(proxyBeanMethods = false)
class TimeConfig {

	@Bean
	Clock clock() {
		return Clock.systemUTC();
	}

}
