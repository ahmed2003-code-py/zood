package com.zawed.api.platform.config;

import org.springframework.context.annotation.Configuration;
import org.springframework.scheduling.annotation.EnableScheduling;

/**
 * In-process periodic work (for now, sweeping expired rate-limit windows). Jobs that must run once
 * across several instances will take a database lock (ADR 0011) when they arrive.
 */
@Configuration(proxyBeanMethods = false)
@EnableScheduling
class SchedulingConfig {

}
