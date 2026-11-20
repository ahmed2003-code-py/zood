package com.zawed.api.platform.jobs;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.net.http.HttpResponse;
import java.sql.Connection;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.function.Function;
import java.util.stream.Collectors;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.health.actuate.endpoint.HttpCodeStatusMapper;
import org.springframework.boot.health.actuate.endpoint.StatusAggregator;
import org.springframework.boot.health.contributor.Health;
import org.springframework.boot.health.contributor.Status;
import org.springframework.core.env.Environment;
import org.springframework.dao.DataAccessResourceFailureException;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.transaction.support.TransactionTemplate;

import tools.jackson.databind.ObjectMapper;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.jobs.JobResult.Outcome;
import com.zawed.api.platform.jobs.JobsHealth.State;
import com.zawed.api.platform.security.Role;
import com.zawed.api.support.Owner;

/**
 * The run record and liveness of scheduled jobs (V21, ADR 0028). Each test declares its own jobs in a
 * registry of its own, under names no real job has, so nothing here changes the application's
 * health answer for the other tests.
 */
class JobsTests extends IdentityTestSupport {

	/** Every job the application declares (ArchitectureTests keeps the @Scheduled list closed). */
	static final Set<String> APPLICATION_JOBS = Set.of("ledger-reconciliation", "nonce-retention",
			"idempotency-key-retention", "session-retention", "job-run-retention");

	@Autowired
	JobRunStore store;

	@Autowired
	TransactionTemplate tx;

	@Autowired
	IdGenerator ids;

	@Autowired
	Clock clock;

	@Autowired
	Environment environment;

	@Autowired
	JdbcClient jdbc;

	@Autowired
	ObjectMapper json;

	@Autowired
	JobRunRetention runRetention;

	@Autowired
	JobsHealthIndicator indicator;

	@Autowired
	StatusAggregator aggregator;

	@Autowired
	HttpCodeStatusMapper httpCodes;

	// ---------------------------------------------------------------------------------- recording

	@Test
	void aRunIsRecordedWithItsOutcomeTimingsInstanceAndCounts() {
		Jobs jobs = registry();
		Job job = jobs.define(name("ok"), Duration.ofHours(1));

		JobResult result = jobs.run(job, () -> JobResult.success(Map.of("deleted", 3L, "kept", 0L)));

		assertThat(result.outcome()).isEqualTo(Outcome.SUCCESS);
		assertThat(Owner.<String>queryOne("""
				select outcome || '|' || counts::text || '|' || (finished_at >= started_at) || '|' || coalesce(error_class, '-')
				from job_runs where job_name = ?
				""", job.name())).isEqualTo("SUCCESS|{\"kept\": 0, \"deleted\": 3}|true|-");
		assertThat(Owner.<String>queryOne("select instance_id from job_runs where job_name = ?", job.name()))
			.matches("[A-Za-z0-9@._:-]{1,128}");
	}

	@Test
	void aFailureIsCaughtAndRecordedWithItsClassNameOnly() {
		Jobs jobs = registry();
		Job job = jobs.define(name("failing"), Duration.ofHours(1));

		JobResult result = jobs.run(job, () -> {
			throw new IllegalStateException("Failing row contains (plate ABC 123, 01001234567)");
		});

		assertThat(result.outcome()).isEqualTo(Outcome.FAILED);
		String row = Owner.queryOne("select row_to_json(j)::text from job_runs j where job_name = ?", job.name());
		assertThat(row).contains("\"outcome\":\"FAILED\"")
			.contains("\"error_class\":\"java.lang.IllegalStateException\"")
			.doesNotContain("plate")
			.doesNotContain("ABC")
			.doesNotContain("0100");
	}

	@Test
	void aSkipBecauseAnotherInstanceHoldsTheLockIsRecordedAsSuch() {
		Jobs jobs = registry();
		Job job = jobs.define(name("skipped"), Duration.ofHours(1));

		assertThat(jobs.run(job, JobResult::skippedLocked).outcome()).isEqualTo(Outcome.SKIPPED_LOCKED);
		assertThat(Owner.<String>queryOne("select outcome from job_runs where job_name = ?", job.name()))
			.isEqualTo("SKIPPED_LOCKED");
	}

	@Test
	void aRecordThatCannotBeWrittenNeverChangesWhatTheJobDid() {
		JobRunStore down = new JobRunStore(jdbc, json) {
			@Override
			void record(UUID id, String job, String instance, Instant started, Instant finished, Outcome outcome,
					Map<String, Long> counts, String errorClass) {
				throw new DataAccessResourceFailureException("the database is down");
			}
		};
		Jobs jobs = new Jobs(down, tx, ids, clock, environment);
		Job job = jobs.define(name("unrecorded"), Duration.ofHours(1));
		int[] ran = { 0 };

		JobResult result = jobs.run(job, () -> {
			ran[0]++;
			return JobResult.success(Map.of("deleted", 1L));
		});

		assertThat(ran[0]).isEqualTo(1);
		assertThat(result.outcome()).isEqualTo(Outcome.SUCCESS);
	}

	@Test
	void namesAndCountsAreCheckedInTheApplication() {
		Jobs jobs = registry();
		assertThatThrownBy(() -> jobs.define("Not A Name", Duration.ofHours(1)))
			.isInstanceOf(IllegalArgumentException.class);
		Job job = jobs.define(name("twice"), Duration.ofHours(1));
		assertThatThrownBy(() -> jobs.define(job.name(), Duration.ofHours(1))).isInstanceOf(IllegalStateException.class);
		assertThatThrownBy(() -> JobResult.success(Map.of("Plate ABC", 1L))).isInstanceOf(IllegalArgumentException.class);
		assertThatThrownBy(() -> JobResult.success(Map.of("deleted", -1L))).isInstanceOf(IllegalArgumentException.class);
		Map<String, Long> many = new HashMap<>();
		for (int i = 0; i < 17; i++) {
			many.put("c" + i, 1L);
		}
		assertThatThrownBy(() -> JobResult.success(many)).isInstanceOf(IllegalArgumentException.class);
		assertThat(Jobs.instanceId("12345@zawed api/host")).isEqualTo("12345@zawed-api-host");
		assertThat(Jobs.errorClass(new IllegalStateException("x"))).isEqualTo("java.lang.IllegalStateException");
	}

	// ------------------------------------------------------------------------- database checks

	@Test
	void theDatabaseRefusesAnythingButNumbersInCountsAndAClassNameAsTheError() throws SQLException {
		String job = name("checks");
		String[] badCounts = { "'{\"plate\": \"ABC 123\"}'", "'{\"a\": 1.5}'", "'{\"a\": -1}'", "'{\"a\": {\"b\": 1}}'",
				"'[1, 2]'", "'\"text\"'", "'{\"Bad Key\": 1}'", "'{\"a\": true}'",
				"'" + manyCounts(17) + "'" };
		for (String counts : badCounts) {
			assertRefused("'SUCCESS', " + counts + "::jsonb, null", job, "now()", counts);
		}
		// The error is a class name: no spaces, no colon, no message.
		assertRefused("'FAILED', '{}'::jsonb, 'java.lang.IllegalStateException: Failing row contains'", job, "now()",
				"an error message");
		assertRefused("'FAILED', '{}'::jsonb, null", job, "now()", "a failure without its class");
		assertRefused("'SUCCESS', '{}'::jsonb, 'java.lang.Exception'", job, "now()", "a success with an error");
		assertRefused("'DONE', '{}'::jsonb, null", job, "now()", "an unknown outcome");
		assertRefused("'SUCCESS', '{}'::jsonb, null", job, "now() + interval '1 day'", "a run finishing in the future");
		assertRefused("'SUCCESS', '{}'::jsonb, null", "Bad Name", "now()", "a job name with spaces");

		try (Connection connection = app("PLATFORM"); Statement sql = connection.createStatement()) {
			sql.executeQuery(record(job, "now()", "'SUCCESS', '" + manyCounts(16) + "'::jsonb, null")).close();
			connection.commit();
		}
		assertThat(Owner.<Long>queryOne("select count(*) from job_runs where job_name = ?", job)).isEqualTo(1L);
	}

	@Test
	void theRuntimeRoleWritesOnlyThroughTheFunctionAndReadsOnlyInThePlatformScope() throws SQLException {
		Jobs jobs = registry();
		Job job = jobs.define(name("visible"), Duration.ofHours(1));
		jobs.run(job, () -> JobResult.success(Map.of()));

		for (String scope : new String[] { "PLATFORM", "COMPANY", null }) {
			for (String write : new String[] {
					"insert into job_runs (id, job_name, instance_id, started_at, finished_at, outcome) "
							+ "values (gen_random_uuid(), 'forged', 'x', now(), now(), 'SUCCESS')",
					"update job_runs set outcome = 'SUCCESS' where false", "delete from job_runs where false" }) {
				// A connection per attempt: a rollback would also undo its SET ROLE.
				try (Connection connection = app(scope); Statement sql = connection.createStatement()) {
					assertThatThrownBy(() -> sql.executeUpdate(write)).as("%s as %s", write, scope)
						.hasMessageContaining("permission denied");
				}
			}
		}
		assertThat(Owner.<Long>queryOne("select count(*) from job_runs where job_name = 'forged'")).isZero();
		assertThat(visibleRuns("PLATFORM", job.name())).isEqualTo(1L);
		assertThat(visibleRuns("COMPANY", job.name())).isZero();
		assertThat(visibleRuns("OPERATOR", job.name())).isZero();
		assertThat(visibleRuns(null, job.name())).isZero();
	}

	@Test
	void runsArePurgedAfterTheRetentionAndNeverInsideThirtyDays() throws SQLException {
		String job = name("old");
		UUID old = plantRun(job, "now() - interval '100 days'", "SUCCESS", null);
		UUID recent = plantRun(job, "now() - interval '40 days'", "SUCCESS", null);

		try (Connection connection = app("PLATFORM"); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeQuery("select purge_job_runs(now() - interval '29 days', 100)"))
				.hasMessageContaining("30-day minimum");
		}
		assertThat(runRetention.purge()).isGreaterThanOrEqualTo(1);
		assertThat(Owner.<UUID>queryList("select id from job_runs where job_name = ?", job)).containsExactly(recent)
			.doesNotContain(old);
		assertThat(JobRunRetention.MINIMUM).isEqualTo(Duration.ofDays(31));
	}

	// ----------------------------------------------------------------------------------- liveness

	@Test
	void aJobWithNoSuccessInItsWindowIsOverdueAndTheHealthTurnsDown() {
		Jobs jobs = registry();
		Job overdue = jobs.define(name("overdue"), Duration.ofHours(26));
		Job failing = jobs.define(name("failing"), Duration.ofHours(1));
		Job ok = jobs.define(name("ok"), Duration.ofHours(1));
		Job pending = jobs.define(name("pending"), Duration.ofHours(1));
		// The reconciliation case: its last success was 27 hours ago, and nothing has run since.
		plantRun(overdue.name(), "now() - interval '27 hours'", "SUCCESS", null);
		plantRun(failing.name(), "now() - interval '30 minutes'", "SUCCESS", null);
		plantRun(failing.name(), "now() - interval '10 minutes'", "FAILED", "java.lang.IllegalStateException");
		plantRun(ok.name(), "now() - interval '40 minutes'", "FAILED", "java.lang.IllegalStateException");
		plantRun(ok.name(), "now() - interval '5 minutes'", "SUCCESS", null);
		JobsHealth health = new JobsHealth(jobs, store, tx, clock);

		Map<String, JobsHealth.Status> statuses = health.statuses()
			.stream()
			.collect(Collectors.toMap((s) -> s.job().name(), Function.identity()));

		assertThat(statuses.get(overdue.name()).state()).isEqualTo(State.OVERDUE);
		assertThat(statuses.get(failing.name()).state()).isEqualTo(State.FAILING);
		assertThat(statuses.get(failing.name()).lastFailureErrorClass()).isEqualTo("java.lang.IllegalStateException");
		assertThat(statuses.get(ok.name()).state()).isEqualTo(State.OK);
		assertThat(statuses.get(pending.name()).state()).isEqualTo(State.PENDING);
		assertThat(JobsHealthIndicator.of(health.statuses()).getStatus()).isEqualTo(Status.DOWN);

		List<JobsHealth.Status> withoutOverdue = health.statuses()
			.stream()
			.filter((s) -> s.state() != State.OVERDUE)
			.toList();
		assertThat(JobsHealthIndicator.of(withoutOverdue).getStatus()).isEqualTo(JobsHealthIndicator.WARN);
		List<JobsHealth.Status> healthy = withoutOverdue.stream().filter((s) -> s.state() != State.FAILING).toList();
		assertThat(JobsHealthIndicator.of(healthy).getStatus()).isEqualTo(Status.UP);
	}

	@Test
	void theWindowIsJudgedAtItsBoundary() {
		Instant success = Instant.parse("2026-11-20T00:30:00Z");
		Instant due = JobsHealth.dueBy(Duration.ofHours(26), success, Instant.parse("2026-11-01T00:00:00Z"));
		assertThat(due).isEqualTo(Instant.parse("2026-11-21T02:30:00Z"));
		assertThat(JobsHealth.evaluate(due, success, null, due)).isEqualTo(State.OK);
		assertThat(JobsHealth.evaluate(due, success, null, due.plusNanos(1))).isEqualTo(State.OVERDUE);
		// A job that never succeeded counts from the instance's start.
		Instant started = Instant.parse("2026-11-20T10:00:00Z");
		Instant firstDue = JobsHealth.dueBy(Duration.ofHours(1), null, started);
		assertThat(JobsHealth.evaluate(firstDue, null, null, started.plusSeconds(60))).isEqualTo(State.PENDING);
		assertThat(JobsHealth.evaluate(firstDue, null, null, started.plusSeconds(3601))).isEqualTo(State.OVERDUE);
		assertThat(JobsHealth.evaluate(firstDue, null, started.plusSeconds(30), started.plusSeconds(60)))
			.isEqualTo(State.FAILING);
	}

	@Test
	void warnAnswers200DownAnswers503AndDownOutranksWarn() {
		assertThat(httpCodes.getStatusCode(JobsHealthIndicator.WARN)).isEqualTo(200);
		// The readiness probe (database down) keeps its 503: the custom mapping did not replace it.
		assertThat(httpCodes.getStatusCode(Status.DOWN)).isEqualTo(503);
		assertThat(httpCodes.getStatusCode(Status.OUT_OF_SERVICE)).isEqualTo(503);
		assertThat(httpCodes.getStatusCode(Status.UP)).isEqualTo(200);
		assertThat(aggregator.getAggregateStatus(Set.of(Status.UP, JobsHealthIndicator.WARN)))
			.isEqualTo(JobsHealthIndicator.WARN);
		assertThat(aggregator.getAggregateStatus(Set.of(JobsHealthIndicator.WARN, Status.DOWN))).isEqualTo(Status.DOWN);
	}

	@Test
	void theApplicationsIndicatorCoversEveryDeclaredJob() {
		Health health = indicator.health();
		assertThat(health).isNotNull();
		assertThat(health.getDetails().keySet()).containsExactlyInAnyOrderElementsOf(APPLICATION_JOBS);
	}

	// --------------------------------------------------------------------------------- Ops console

	@Test
	void operationsReadsEachJobsLastSuccessAndTheRunHistory() throws Exception {
		Jobs jobs = registry();
		Job job = jobs.define(name("history"), Duration.ofHours(1));
		jobs.run(job, () -> JobResult.success(Map.of("deleted", 7L)));
		jobs.run(job, () -> {
			throw new IllegalArgumentException("row data");
		});
		SignedIn ops = signIn(seed(Role.OPS));

		HttpResponse<String> list = authed("GET", "/api/v1/ops/jobs", ops.accessToken(), null);
		assertThat(list.statusCode()).as(list.body()).isEqualTo(200);
		List<String> names = JsonPath.read(list.body(), "$[*].job");
		assertThat(names).containsExactlyInAnyOrderElementsOf(APPLICATION_JOBS);
		assertThat(JsonPath.<List<String>>read(list.body(), "$[?(@.job == 'ledger-reconciliation')].maxAge"))
			.containsExactly("PT26H");

		HttpResponse<String> runs = authed("GET", "/api/v1/ops/jobs/runs?job=" + job.name(), ops.accessToken(), null);
		assertThat(runs.statusCode()).as(runs.body()).isEqualTo(200);
		assertThat(JsonPath.<List<String>>read(runs.body(), "$.items[*].outcome")).containsExactly("FAILED", "SUCCESS");
		assertThat(JsonPath.<Integer>read(runs.body(), "$.items[1].counts.deleted")).isEqualTo(7);
		assertThat(JsonPath.<String>read(runs.body(), "$.items[0].errorClass"))
			.isEqualTo("java.lang.IllegalArgumentException");
		assertThat(runs.body()).doesNotContain("row data");

		HttpResponse<String> bad = authed("GET", "/api/v1/ops/jobs/runs?job=Not%20A%20Job", ops.accessToken(), null);
		assertThat(bad.statusCode()).isEqualTo(400);

		SignedIn company = signIn(seed(Role.FLEET_MANAGER));
		assertThat(authed("GET", "/api/v1/ops/jobs", company.accessToken(), null).statusCode()).isEqualTo(403);
		assertThat(authed("GET", "/api/v1/ops/jobs/runs", company.accessToken(), null).statusCode()).isEqualTo(403);
	}

	// ----------------------------------------------------------------------------------- helpers

	/** A registry of this test's own, so its jobs never enter the application's health answer. */
	private Jobs registry() {
		return new Jobs(store, tx, ids, clock, environment);
	}

	private static String name(String what) {
		return "test-" + what + "-" + UUID.randomUUID().toString().substring(0, 8);
	}

	private static UUID plantRun(String job, String startedAt, String outcome, String errorClass) {
		UUID id = UUID.randomUUID();
		Owner.update("""
				insert into job_runs (id, job_name, instance_id, started_at, finished_at, outcome, counts, error_class)
				values (?, ?, 'test@host', %1$s, %1$s + interval '1 second', ?, '{}'::jsonb, ?)
				""".formatted(startedAt), id, job, outcome, errorClass);
		return id;
	}

	private static String manyCounts(int n) {
		StringBuilder counts = new StringBuilder("{");
		for (int i = 0; i < n; i++) {
			counts.append(i == 0 ? "" : ", ").append("\"c").append(i).append("\": ").append(i);
		}
		return counts.append('}').toString();
	}

	private static String record(String job, String finished, String outcomeCountsError) {
		return "select job_run_record(gen_random_uuid(), '" + job + "', 'test@host', now() - interval '1 second', "
				+ finished + ", " + outcomeCountsError + ")";
	}

	private static void assertRefused(String outcomeCountsError, String job, String finished, String what)
			throws SQLException {
		try (Connection connection = app(null); Statement sql = connection.createStatement()) {
			// check_violation: a table check, or the function's own refusal of a future finish.
			assertThatThrownBy(() -> sql.executeQuery(record(job, finished, outcomeCountsError))).as(what)
				.isInstanceOfSatisfying(SQLException.class, (ex) -> assertThat(ex.getSQLState()).as(what).isEqualTo("23514"));
		}
	}

	private static long visibleRuns(String scope, String job) throws SQLException {
		try (Connection connection = app(scope); Statement sql = connection.createStatement();
				ResultSet rs = sql.executeQuery("select count(*) from job_runs where job_name = '" + job + "'")) {
			rs.next();
			return rs.getLong(1);
		}
	}

	/** A transaction as the runtime role, told what {@code ScopedTransactionManager} would tell it. */
	private static Connection app(String scopeType) throws SQLException {
		Connection connection = Owner.connect();
		connection.setAutoCommit(false);
		try (Statement sql = connection.createStatement()) {
			sql.execute("SET ROLE zawed_app");
			if (scopeType != null) {
				sql.execute("select set_config('app.scope_type', '" + scopeType + "', true)");
				if (!"PLATFORM".equals(scopeType)) {
					sql.execute("select set_config('app.scope_id', '" + UUID.randomUUID() + "', true)");
				}
			}
		}
		return connection;
	}

}
