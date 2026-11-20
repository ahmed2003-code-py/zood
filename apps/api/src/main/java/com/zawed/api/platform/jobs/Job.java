package com.zawed.api.platform.jobs;

import java.time.Duration;

/**
 * A scheduled job as its run record and the health check know it (ADR 0028).
 *
 * @param name the job's stable name in {@code job_runs}, e.g. {@code ledger-reconciliation}
 * @param maxAge how long the job may go without a success before its health is DOWN;
 * {@code zawed.jobs.max-age.<name>} overrides the job's default
 */
public record Job(String name, Duration maxAge) {

}
