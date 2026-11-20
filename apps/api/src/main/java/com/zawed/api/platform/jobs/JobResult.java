package com.zawed.api.platform.jobs;

import java.util.Map;
import java.util.Set;

/**
 * What one run of a job came to. Counts are numbers only, under the names in {@link #COUNT_KEYS}
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

	/**
	 * The only count names a run may record: the same fixed list as V21's {@code job_run_count_keys()}
	 * (JobsTests checks they agree). A new count is added to both.
	 */
	public static final Set<String> COUNT_KEYS = Set.of("columnErrors", "deleted", "failed", "findings",
			"mfaChallenges", "payables", "reencrypted", "remaining", "sessions", "wallets");

	public JobResult {
		counts = Map.copyOf(counts);
		for (var entry : counts.entrySet()) {
			if (!COUNT_KEYS.contains(entry.getKey()) || entry.getValue() < 0) {
				throw new IllegalArgumentException("A job count is a non-negative number under a listed name");
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
