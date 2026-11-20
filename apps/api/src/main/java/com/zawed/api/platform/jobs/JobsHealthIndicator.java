package com.zawed.api.platform.jobs;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.List;

import org.springframework.boot.health.contributor.Health;
import org.springframework.boot.health.contributor.HealthIndicator;
import org.springframework.boot.health.contributor.Status;
import org.springframework.stereotype.Component;

import com.zawed.api.platform.jobs.JobsHealth.State;

/**
 * The scheduled jobs in {@code /api/v1/health} (ADR 0028): DOWN when any job has had no success
 * within its window, WARN when a job's latest attempt failed but its last success is still recent,
 * UP otherwise. WARN answers 200 (application.yaml maps it), DOWN answers 503.
 *
 * <p>It is part of the overall health only. The readiness probe that Railway checks on deploy
 * includes just the database and the application state, so a late job never stops a deploy or
 * takes an instance out of service; it is a signal for whoever watches the health URL. Details are
 * never shown there ({@code show-details: never}); the Ops console reads them from
 * {@code GET /api/v1/ops/jobs}.
 *
 * <p>The health URL is public, so the answer is kept for {@link #CACHE} instead of querying the
 * database on every call.
 */
// Bean "jobsHealthIndicator": the health contributor is named "jobs".
@Component
class JobsHealthIndicator implements HealthIndicator {

	/** No description: the public health answer carries the code only. */
	static final Status WARN = new Status("WARN");

	static final Duration CACHE = Duration.ofSeconds(30);

	private final JobsHealth health;
	private final Clock clock;

	private volatile Cached cached;

	private record Cached(Health health, Instant at) {
	}

	JobsHealthIndicator(JobsHealth health, Clock clock) {
		this.health = health;
		this.clock = clock;
	}

	@Override
	public Health health() {
		Cached current = cached;
		Instant now = clock.instant();
		if (current != null && now.isBefore(current.at().plus(CACHE))) {
			return current.health();
		}
		Health answer;
		try {
			answer = of(health.statuses());
		}
		catch (RuntimeException ex) {
			// The database indicator already reports an unreachable database; this one does not guess.
			answer = Health.unknown().withDetail("error", ex.getClass().getSimpleName()).build();
		}
		cached = new Cached(answer, now);
		return answer;
	}

	static Health of(List<JobsHealth.Status> statuses) {
		boolean overdue = statuses.stream().anyMatch((s) -> s.state() == State.OVERDUE);
		boolean failing = statuses.stream().anyMatch((s) -> s.state() == State.FAILING);
		Health.Builder builder = overdue ? Health.down() : failing ? Health.status(WARN) : Health.up();
		for (JobsHealth.Status status : statuses) {
			builder.withDetail(status.job().name(), status.state().name());
		}
		return builder.build();
	}

}
