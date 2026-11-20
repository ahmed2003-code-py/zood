package com.zawed.api.platform.jobs;

import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.transaction.support.TransactionTemplate;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.platform.jobs.JobResult.Outcome;
import com.zawed.api.platform.jobs.JobsHealth.State;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.ErrorCodes;
import com.zawed.api.platform.web.Page;

/**
 * Scheduled jobs in the Ops console (ADR 0028, runbook "Reading job health"): the last success of
 * each job against its window, and the run history. Platform roles only; nothing here is tenant data.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/ops/jobs")
@Tag(name = "platform")
class OpsJobsController {

	private final JobsHealth health;
	private final JobRunStore store;
	private final TransactionTemplate tx;

	OpsJobsController(JobsHealth health, JobRunStore store, TransactionTemplate tx) {
		this.health = health;
		this.store = store;
		this.tx = tx;
	}

	@GetMapping
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listJobHealth", summary = "Each scheduled job: its last success, against its window")
	List<JobHealthResponse> jobs() {
		return health.statuses().stream().map(JobHealthResponse::of).toList();
	}

	@GetMapping("/runs")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listJobRuns", summary = "Job runs, newest first; optionally of one job")
	Page<JobRunResponse> runs(@RequestParam(required = false) String job, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		if (job != null && !Jobs.NAME.matcher(job).matches()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, ErrorCodes.VALIDATION_FAILED, "Not a job name", "job", null);
		}
		int size = Page.limit(limit);
		UUID after = Page.cursor(cursor);
		// In a transaction, so the platform scope reaches row-level security on job_runs.
		List<JobRunStore.Run> rows = tx.execute((status) -> store.runs(job, after, size + 1));
		Page<JobRunStore.Run> page = Page.of(rows, size, JobRunStore.Run::id);
		return new Page<>(page.items().stream().map(JobRunResponse::of).toList(), page.nextCursor());
	}

	record JobHealthResponse(@Schema(requiredMode = RequiredMode.REQUIRED) String job,
			@Schema(requiredMode = RequiredMode.REQUIRED,
					description = "OK; PENDING (not run yet since start, still in its window); FAILING (latest attempt "
							+ "failed, last success still in its window); OVERDUE (no success within its window)") State state,
			@Schema(requiredMode = RequiredMode.REQUIRED,
					description = "How long the job may go without a success: an ISO-8601 duration such as PT26H") String maxAge,
			@Schema(requiredMode = RequiredMode.REQUIRED,
					description = "When the job becomes OVERDUE without another success") Instant dueBy,
			Instant lastStartedAt, Outcome lastOutcome, Instant lastSuccessAt,
			@Schema(description = "The last success's counts: numbers only") Map<String, Long> lastSuccessCounts,
			Instant lastFailureAt,
			@Schema(description = "The last failure's exception class; never its message") String lastFailureErrorClass) {

		static JobHealthResponse of(JobsHealth.Status s) {
			return new JobHealthResponse(s.job().name(), s.state(), s.job().maxAge().toString(), s.dueBy(), s.lastStartedAt(),
					s.lastOutcome(), s.lastSuccessAt(), s.lastSuccessCounts(), s.lastFailureAt(),
					s.lastFailureErrorClass());
		}

	}

	record JobRunResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String job,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "pid@host of the instance") String instance,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant startedAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant finishedAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) Outcome outcome,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Numbers only") Map<String, Long> counts,
			@Schema(description = "FAILED only: the exception class, never its message") String errorClass) {

		static JobRunResponse of(JobRunStore.Run r) {
			return new JobRunResponse(r.id(), r.job(), r.instance(), r.startedAt(), r.finishedAt(), r.outcome(), r.counts(),
					r.errorClass());
		}

	}

}
