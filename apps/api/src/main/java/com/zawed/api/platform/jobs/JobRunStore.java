package com.zawed.api.platform.jobs;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Component;

import tools.jackson.core.type.TypeReference;
import tools.jackson.databind.ObjectMapper;

import com.zawed.api.platform.jobs.JobResult.Outcome;

/**
 * The SQL behind {@code job_runs} (V21). Writes and the health summary go through the table's
 * SECURITY DEFINER functions, because a scheduled job and the health indicator have no principal;
 * the run history is read from the table itself, under row-level security (platform scope only).
 */
@Component
class JobRunStore {

	private static final TypeReference<Map<String, Long>> COUNTS = new TypeReference<>() {
	};

	private final JdbcClient jdbc;
	private final ObjectMapper json;

	JobRunStore(JdbcClient jdbc, ObjectMapper json) {
		this.jdbc = jdbc;
		this.json = json;
	}

	void record(UUID id, String job, String instance, Instant started, Instant finished, Outcome outcome,
			Map<String, Long> counts, String errorClass) {
		jdbc.sql("""
				select job_run_record(:id, :job, :instance, :started, :finished, :outcome, cast(:counts as jsonb), :error)
				""")
			.param("id", id)
			.param("job", job)
			.param("instance", instance)
			.param("started", ts(started))
			.param("finished", ts(finished))
			.param("outcome", outcome.name())
			.param("counts", json.writeValueAsString(counts))
			.param("error", errorClass)
			.query((rs, n) -> null)
			.list();
	}

	/** The latest run, success and failure of each job named; a job that never ran has nulls. */
	record Latest(String job, Instant lastStartedAt, Instant lastFinishedAt, Outcome lastOutcome, Instant lastSuccessAt,
			Map<String, Long> lastSuccessCounts, Instant lastFailureAt, String lastFailureErrorClass) {
	}

	List<Latest> latest(List<String> jobs) {
		// Job names never contain a comma (V21's name check), so one text parameter carries the list.
		return jdbc.sql("select * from job_run_latest(string_to_array(:names, ','))")
			.param("names", String.join(",", jobs))
			.query((rs, n) -> new Latest(rs.getString("job_name"), instant(rs, "last_started_at"),
					instant(rs, "last_finished_at"), outcome(rs, "last_outcome"), instant(rs, "last_success_at"),
					counts(rs.getString("last_success_counts")), instant(rs, "last_failure_at"),
					rs.getString("last_failure_error_class")))
			.list();
	}

	record Run(UUID id, String job, String instance, Instant startedAt, Instant finishedAt, Outcome outcome,
			Map<String, Long> counts, String errorClass) {
	}

	/** Newest first; {@code job} null for every job. Platform scope only (RLS). */
	List<Run> runs(String job, UUID cursor, int limit) {
		return jdbc.sql("""
				select * from job_runs
				where (cast(:job as text) is null or job_name = :job)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("job", job)
			.param("cursor", cursor)
			.param("limit", limit)
			.query(this::run)
			.list();
	}

	/** @return how many runs were deleted */
	int purge(Instant before, int limit) {
		Integer deleted = jdbc.sql("select purge_job_runs(:before, :limit)")
			.param("before", ts(before))
			.param("limit", limit)
			.query(Integer.class)
			.single();
		return deleted == null ? 0 : deleted;
	}

	private Run run(ResultSet rs, int row) throws SQLException {
		return new Run(rs.getObject("id", UUID.class), rs.getString("job_name"), rs.getString("instance_id"),
				instant(rs, "started_at"), instant(rs, "finished_at"), outcome(rs, "outcome"),
				counts(rs.getString("counts")), rs.getString("error_class"));
	}

	private static Outcome outcome(ResultSet rs, String column) throws SQLException {
		String value = rs.getString(column);
		return value == null ? null : Outcome.valueOf(value);
	}

	private Map<String, Long> counts(String value) {
		return value == null ? null : json.readValue(value, COUNTS);
	}

}
