package com.zawed.api.platform.jobs;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.function.Function;
import java.util.stream.Collectors;

import org.springframework.stereotype.Component;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.platform.jobs.JobResult.Outcome;
import com.zawed.api.platform.jobs.JobRunStore.Latest;

/**
 * "Last success per job" (ADR 0028), for the health indicator and the Ops console. A job is judged
 * against its own {@link Job#maxAge}: the time it may go without a success, set well above its
 * schedule (reconciliation runs nightly and may go 27 hours).
 */
@Component
class JobsHealth {

	enum State {

		/** Succeeded within its window, and nothing failed since. */
		OK,
		/** Has not run yet since this instance started, and its window from the start is still open. */
		PENDING,
		/** Its latest attempt failed, but a success is still within the window. */
		FAILING,
		/** No success within the window: the job is not doing its work. */
		OVERDUE

	}

	record Status(Job job, State state, Instant dueBy, Instant lastStartedAt, Outcome lastOutcome,
			Instant lastSuccessAt, Map<String, Long> lastSuccessCounts, Instant lastFailureAt,
			String lastFailureErrorClass) {
	}

	private final Jobs jobs;
	private final JobRunStore store;
	private final TransactionTemplate tx;
	private final Clock clock;

	JobsHealth(Jobs jobs, JobRunStore store, TransactionTemplate tx, Clock clock) {
		this.jobs = jobs;
		this.store = store;
		this.tx = tx;
		this.clock = clock;
	}

	/** Every job this application declares, by name, with its state now. */
	List<Status> statuses() {
		List<Job> defined = jobs.defined();
		Map<String, Latest> latest = tx.execute((status) -> store.latest(defined.stream().map(Job::name).toList()))
			.stream()
			.collect(Collectors.toMap(Latest::job, Function.identity()));
		Instant now = clock.instant();
		List<Status> statuses = new ArrayList<>();
		for (Job job : defined) {
			Latest row = latest.get(job.name());
			Instant lastSuccess = row == null ? null : row.lastSuccessAt();
			Instant lastFailure = row == null ? null : row.lastFailureAt();
			Instant dueBy = dueBy(job.maxAge(), lastSuccess, jobs.startedAt());
			statuses.add(new Status(job, evaluate(dueBy, lastSuccess, lastFailure, now), dueBy,
					row == null ? null : row.lastStartedAt(), row == null ? null : row.lastOutcome(), lastSuccess,
					row == null ? null : row.lastSuccessCounts(), lastFailure,
					row == null ? null : row.lastFailureErrorClass()));
		}
		return statuses;
	}

	/**
	 * The latest a next success may come: the window counts from the last success, or, for a job
	 * that has never succeeded, from this instance's start (a new deploy is not late yet).
	 */
	static Instant dueBy(Duration maxAge, Instant lastSuccess, Instant instanceStarted) {
		return (lastSuccess != null ? lastSuccess : instanceStarted).plus(maxAge);
	}

	static State evaluate(Instant dueBy, Instant lastSuccess, Instant lastFailure, Instant now) {
		if (now.isAfter(dueBy)) {
			return State.OVERDUE;
		}
		if (lastFailure != null && (lastSuccess == null || lastFailure.isAfter(lastSuccess))) {
			return State.FAILING;
		}
		return lastSuccess == null ? State.PENDING : State.OK;
	}

}
