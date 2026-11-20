package com.zawed.api.platform.jobs;

import java.util.Map;
import java.util.regex.Pattern;

/**
 * What one run of a job came to. Counts are numbers only, under short identifier keys
 * ({@code sessions}, {@code mfaChallenges}): never a value, a name or a message, so no row data can
 * reach {@code job_runs} (V21 checks the same rule).
 */
public record JobResult(Outcome outcome, Map<String, Long> counts) {

	public enum Outcome {

		SUCCESS,
		/** The body threw; {@link Jobs#run} records the exception's class name only. */
		FAILED,
		/** The job's own lock or lease said another instance runs it, or already ran it. */
		SKIPPED_LOCKED

	}

	/** The same rule as V21's {@code job_run_counts_valid}. */
	static final Pattern COUNT_KEY = Pattern.compile("[a-z][A-Za-z0-9]{0,39}");

	static final int MAX_COUNTS = 16;

	public JobResult {
		counts = Map.copyOf(counts);
		if (counts.size() > MAX_COUNTS) {
			throw new IllegalArgumentException("A job run records at most " + MAX_COUNTS + " counts");
		}
		for (var entry : counts.entrySet()) {
			if (!COUNT_KEY.matcher(entry.getKey()).matches() || entry.getValue() < 0) {
				throw new IllegalArgumentException("A job count is a non-negative number under an identifier key");
			}
		}
	}

	public static JobResult success(Map<String, Long> counts) {
		return new JobResult(Outcome.SUCCESS, counts);
	}

	public static JobResult skippedLocked() {
		return new JobResult(Outcome.SKIPPED_LOCKED, Map.of());
	}

	static JobResult failed() {
		return new JobResult(Outcome.FAILED, Map.of());
	}

}
