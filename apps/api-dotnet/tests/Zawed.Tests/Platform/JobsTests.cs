using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Zawed.Api.Platform.Health;
using Zawed.Api.Platform.Jobs;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Platform;

/// <summary>
/// The run record and liveness of scheduled jobs (V21, ADR 0028; Java <c>JobsTests</c>). Each test declares its own jobs
/// in a registry of its own, under names no real job has, so nothing here changes the application's health answer for
/// the other tests. Before the identity module lands, the Ops console is called with a minted token
/// (<see cref="ProbeApi.TokenFor"/>).
/// </summary>
public sealed class JobsTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    /// <summary>
    /// The jobs declared so far. Java's list (<c>APPLICATION_JOBS</c>) also has the jobs of network, ledger,
    /// notifications and leads; each module's port declares its own, and the Ops console must list every declared job.
    /// </summary>
    private static readonly string[] PlatformJobs =
        ["data-key-rotation", "idempotency-key-retention", "job-run-retention", "rate-limit-retention", "session-retention"];

    private Owner Owner => api.Owner;

    private IServiceProvider Services => api.Factory.Services;

    /// <summary>A registry of this test's own; its clock moves forward a second per reading, so runs are ordered.</summary>
    private Jobs Registry(ITransactions? transactions = null) =>
        new(transactions ?? Services.GetRequiredService<ITransactions>(), new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-1))
        {
            AutoAdvanceAmount = TimeSpan.FromSeconds(1),
        }, Services.GetRequiredService<IConfiguration>(), Services.GetRequiredService<ILogger<Jobs>>());

    private static string Name(string what) => $"test-{what}-{Guid.NewGuid().ToString()[..8]}";

    private static Dictionary<string, long> Counts(params (string Key, long Value)[] counts) => counts.ToDictionary(c => c.Key, c => c.Value);

    // ----------------------------------------------------------------------------------- recording

    [Fact]
    public async Task ARunIsRecordedWithItsOutcomeTimingsInstanceAndCounts()
    {
        var jobs = Registry();
        var job = jobs.Define(Name("ok"), TimeSpan.FromHours(1));

        var result = await jobs.RunAsync(job, _ => Task.FromResult(JobResult.Success(Counts(("deleted", 3), ("failed", 0)))), default);

        Assert.Equal(JobOutcome.SUCCESS, result.Outcome);
        Assert.Equal("SUCCESS|{\"failed\": 0, \"deleted\": 3}|true|-", await Owner.QueryOneAsync<string>("""
            select outcome || '|' || counts::text || '|' || (finished_at >= started_at) || '|' || coalesce(error_class, '-')
            from job_runs where job_name = @name
            """, new { name = job.Name }));
        Assert.Matches("^[A-Za-z0-9@._:-]{1,128}$", await Owner.QueryOneAsync<string>("select instance_id from job_runs where job_name = @name",
            new { name = job.Name }));
    }

    [Fact]
    public async Task AFailureIsCaughtAndRecordedWithItsClassNameOnly()
    {
        var jobs = Registry();
        var job = jobs.Define(Name("failing"), TimeSpan.FromHours(1));

        var result = await jobs.RunAsync(job, _ => throw new InvalidOperationException("Failing row contains (plate ABC 123, 01001234567)"), default);

        Assert.Equal(JobOutcome.FAILED, result.Outcome);
        var row = await Owner.QueryOneAsync<string>("select row_to_json(j)::text from job_runs j where job_name = @name", new { name = job.Name });
        Assert.Contains("\"outcome\":\"FAILED\"", row);
        Assert.Contains("\"error_class\":\"System.InvalidOperationException\"", row);
        Assert.DoesNotContain("plate", row);
        Assert.DoesNotContain("ABC", row);
        Assert.DoesNotContain("0100", row);
    }

    [Fact]
    public async Task ASkipBecauseAnotherInstanceHoldsTheLockIsRecordedAsSuch()
    {
        var jobs = Registry();
        var job = jobs.Define(Name("skipped"), TimeSpan.FromHours(1));

        Assert.Equal(JobOutcome.SKIPPED_LOCKED, (await jobs.RunAsync(job, _ => Task.FromResult(JobResult.SkippedLocked()), default)).Outcome);
        Assert.Equal("SKIPPED_LOCKED", await Owner.QueryOneAsync<string>("select outcome from job_runs where job_name = @name", new { name = job.Name }));
    }

    [Fact]
    public async Task ARecordThatCannotBeWrittenNeverChangesWhatTheJobDid()
    {
        var jobs = Registry(new DatabaseDown());
        var job = jobs.Define(Name("unrecorded"), TimeSpan.FromHours(1));
        var ran = 0;

        var result = await jobs.RunAsync(job, _ =>
        {
            ran++;
            return Task.FromResult(JobResult.Success(Counts(("deleted", 1))));
        }, default);

        Assert.Equal(1, ran);
        Assert.Equal(JobOutcome.SUCCESS, result.Outcome);
    }

    [Fact]
    public void NamesAndCountsAreCheckedInTheApplication()
    {
        var jobs = Registry();
        Assert.Throws<ArgumentException>(() => jobs.Define("Not A Name", TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentException>(() => jobs.Define("name\n", TimeSpan.FromHours(1)));
        var job = jobs.Define(Name("twice"), TimeSpan.FromHours(1));
        Assert.Throws<InvalidOperationException>(() => jobs.Define(job.Name, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentException>(() => JobResult.Success(Counts(("Plate ABC", 1))));
        Assert.Throws<ArgumentException>(() => JobResult.Success(Counts(("deleted", -1))));
        // A well-formed identifier is not enough: a value written as a key is refused.
        Assert.Throws<ArgumentException>(() => JobResult.Success(Counts(("c01001234567", 1))));
        Assert.Equal("12345@zawed-api-host", Jobs.InstanceId("12345@zawed api/host"));
        Assert.Equal("System.InvalidOperationException", Jobs.ErrorClass(new InvalidOperationException("x")));
    }

    [Fact]
    public void AJobsWindowIsOverriddenByTheJavaSettingInEitherSpelling()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zawed:Jobs:MaxAge:ledger-reconciliation"] = "PT30H",
            ["ZAWED_JOBS_MAXAGE_SESSIONRETENTION"] = "2h",
            ["Zawed:Jobs:MaxAge:bad-window"] = "PT0S",
        }).Build();
        var jobs = new Jobs(Services.GetRequiredService<ITransactions>(), TimeProvider.System, configuration, Services.GetRequiredService<ILogger<Jobs>>());
        Assert.Equal(TimeSpan.FromHours(30), jobs.Define("ledger-reconciliation", TimeSpan.FromHours(27)).MaxAge);
        Assert.Equal(TimeSpan.FromHours(2), jobs.Define("session-retention", TimeSpan.FromHours(26)).MaxAge);
        Assert.Equal(TimeSpan.FromHours(1), jobs.Define("other-job", TimeSpan.FromHours(1)).MaxAge);
        Assert.Throws<ArgumentException>(() => jobs.Define("bad-window", TimeSpan.FromHours(1)));
    }

    // ------------------------------------------------------------------------------ database checks

    [Fact]
    public async Task TheDatabaseAndTheApplicationListTheSameCountNames()
    {
        var listed = await Owner.QueryOneAsync<string>("select array_to_string(job_run_count_keys(), ',')");
        Assert.Equal(JobResult.CountKeys.Order(), listed!.Split(',').Order());
    }

    [Fact]
    public async Task TheDatabaseRefusesAnythingButNumbersInCountsAndAClassNameAsTheError()
    {
        var job = Name("checks");
        string[] badCounts =
        [
            "'{\"plate\": \"ABC 123\"}'", "'{\"a\": 1.5}'", "'{\"a\": -1}'", "'{\"a\": {\"b\": 1}}'", "'[1, 2]'", "'\"text\"'", "'{\"Bad Key\": 1}'",
            "'{\"deleted\": true}'", "'{\"deleted\": \"3\"}'",
            // Identifier-shaped keys that are not on the list: a phone number, a plate.
            "'{\"c01001234567\": 1}'", "'{\"plateABC123\": 1}'",
        ];
        foreach (var counts in badCounts)
        {
            await AssertRefused($"'SUCCESS', {counts}::jsonb, null", job, "now()", counts);
        }
        // The error is a class name: no spaces, no colon, no message.
        await AssertRefused("'FAILED', '{}'::jsonb, 'java.lang.IllegalStateException: Failing row contains'", job, "now()", "an error message");
        await AssertRefused("'FAILED', '{}'::jsonb, null", job, "now()", "a failure without its class");
        await AssertRefused("'SUCCESS', '{}'::jsonb, 'java.lang.Exception'", job, "now()", "a success with an error");
        await AssertRefused("'DONE', '{}'::jsonb, null", job, "now()", "an unknown outcome");
        await AssertRefused("'SUCCESS', '{}'::jsonb, null", job, "now() + interval '1 day'", "a run finishing in the future");
        await AssertRefused("'SUCCESS', '{}'::jsonb, null", "Bad Name", "now()", "a job name with spaces");

        var every = "{" + string.Join(", ", JobResult.CountKeys.Order(StringComparer.Ordinal).Select((k, i) => $"\"{k}\": {i}")) + "}";
        var (connection, tx) = await RuntimeConnections.OpenAsync(Owner, "PLATFORM");
        await using (connection)
        await using (tx)
        {
            await using var record = new NpgsqlCommand(Record(job, "now()", $"'SUCCESS', '{every}'::jsonb, null"), connection, tx);
            await record.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }
        Assert.Equal(1L, await Owner.QueryOneAsync<long>("select count(*) from job_runs where job_name = @job", new { job }));
    }

    [Fact]
    public async Task TheRuntimeRoleWritesOnlyThroughTheFunctionAndReadsOnlyInThePlatformScope()
    {
        var jobs = Registry();
        var job = jobs.Define(Name("visible"), TimeSpan.FromHours(1));
        await jobs.RunAsync(job, _ => Task.FromResult(JobResult.Success(Counts())), default);

        foreach (var scope in new[] { "PLATFORM", "COMPANY", null })
        {
            foreach (var write in new[]
            {
                "insert into job_runs (id, job_name, instance_id, started_at, finished_at, outcome) values (gen_random_uuid(), 'forged', 'x', now(), now(), 'SUCCESS')",
                "update job_runs set outcome = 'SUCCESS' where false", "delete from job_runs where false",
            })
            {
                var refusal = await RuntimeConnections.RefusalAsync(Owner, scope, write);
                Assert.True(refusal?.MessageText.Contains("permission denied") == true, $"{write} as {scope}");
            }
        }
        Assert.Equal(0L, await Owner.QueryOneAsync<long>("select count(*) from job_runs where job_name = 'forged'"));
        Assert.Equal(1L, await VisibleRuns("PLATFORM", job.Name));
        Assert.Equal(0L, await VisibleRuns("COMPANY", job.Name));
        Assert.Equal(0L, await VisibleRuns("OPERATOR", job.Name));
        Assert.Equal(0L, await VisibleRuns(null, job.Name));
    }

    [Fact]
    public async Task RunsArePurgedAfterTheRetentionAndNeverInsideThirtyDays()
    {
        var job = Name("old");
        var old = await PlantRun(job, "now() - interval '100 days'", "SUCCESS", null);
        var recent = await PlantRun(job, "now() - interval '40 days'", "SUCCESS", null);

        var refusal = await RuntimeConnections.RefusalAsync(Owner, "PLATFORM", "select purge_job_runs(now() - interval '29 days', 100)");
        Assert.Contains("30-day minimum", refusal!.MessageText);
        // Assert on which rows remain, not on how many this call deleted.
        await Services.GetRequiredService<JobRunRetention>().PurgeAsync();
        var remaining = await Owner.QueryAsync<Guid>("select id from job_runs where job_name = @job", new { job });
        Assert.Equal(new[] { recent }, remaining.ToArray());
        Assert.DoesNotContain(old, remaining);
        Assert.Equal(TimeSpan.FromDays(31), JobRunRetention.Minimum);
    }

    // ------------------------------------------------------------------------------------- liveness

    [Fact]
    public async Task AJobWithNoSuccessInItsWindowIsOverdueAndTheHealthTurnsDown()
    {
        var jobs = Registry();
        var overdue = jobs.Define(Name("overdue"), TimeSpan.FromHours(26));
        var failing = jobs.Define(Name("failing"), TimeSpan.FromHours(1));
        var ok = jobs.Define(Name("ok"), TimeSpan.FromHours(1));
        var pending = jobs.Define(Name("pending"), TimeSpan.FromHours(1));
        // The reconciliation case: its last success was 27 hours ago, and nothing has run since.
        await PlantRun(overdue.Name, "now() - interval '27 hours'", "SUCCESS", null);
        await PlantRun(failing.Name, "now() - interval '30 minutes'", "SUCCESS", null);
        await PlantRun(failing.Name, "now() - interval '10 minutes'", "FAILED", "java.lang.IllegalStateException");
        await PlantRun(ok.Name, "now() - interval '40 minutes'", "FAILED", "java.lang.IllegalStateException");
        await PlantRun(ok.Name, "now() - interval '5 minutes'", "SUCCESS", null);
        var health = new JobsHealth(jobs, Services.GetRequiredService<ITransactions>(), TimeProvider.System);

        var statuses = (await health.StatusesAsync(default)).ToDictionary(s => s.Job.Name);

        Assert.Equal(JobState.OVERDUE, statuses[overdue.Name].State);
        Assert.Equal(JobState.FAILING, statuses[failing.Name].State);
        Assert.Equal("java.lang.IllegalStateException", statuses[failing.Name].LastFailureErrorClass);
        Assert.Equal(JobState.OK, statuses[ok.Name].State);
        Assert.Equal(JobOutcome.SUCCESS, statuses[ok.Name].LastOutcome);
        Assert.Equal(JobState.PENDING, statuses[pending.Name].State);

        Assert.Equal("DOWN", JobsHealthCache.Summarise(statuses.Values));
        var withoutOverdue = statuses.Values.Where(s => s.State != JobState.OVERDUE).ToList();
        Assert.Equal("WARN", JobsHealthCache.Summarise(withoutOverdue));
        Assert.Equal("UP", JobsHealthCache.Summarise(withoutOverdue.Where(s => s.State != JobState.FAILING)));
    }

    [Fact]
    public void TheWindowIsJudgedAtItsBoundary()
    {
        var success = DateTimeOffset.Parse("2026-11-20T00:30:00Z");
        var due = JobsHealth.DueBy(TimeSpan.FromHours(26), success, DateTimeOffset.Parse("2026-11-01T00:00:00Z"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T02:30:00Z"), due);
        Assert.Equal(JobState.OK, JobsHealth.Evaluate(due, success, null, due));
        Assert.Equal(JobState.OVERDUE, JobsHealth.Evaluate(due, success, null, due.AddTicks(1)));
        // A job that never succeeded counts from the instance's start.
        var started = DateTimeOffset.Parse("2026-11-20T10:00:00Z");
        var firstDue = JobsHealth.DueBy(TimeSpan.FromHours(1), null, started);
        Assert.Equal(JobState.PENDING, JobsHealth.Evaluate(firstDue, null, null, started.AddSeconds(60)));
        Assert.Equal(JobState.OVERDUE, JobsHealth.Evaluate(firstDue, null, null, started.AddSeconds(3601)));
        Assert.Equal(JobState.FAILING, JobsHealth.Evaluate(firstDue, null, started.AddSeconds(30), started.AddSeconds(60)));
    }

    [Fact]
    public void DownOutranksWarnAndWarnOutranksUp()
    {
        Assert.Equal("WARN", HealthEndpoints.Aggregate("UP", "WARN"));
        Assert.Equal("DOWN", HealthEndpoints.Aggregate("WARN", "DOWN"));
        Assert.Equal("OUT_OF_SERVICE", HealthEndpoints.Aggregate("UP", "OUT_OF_SERVICE", "WARN"));
    }

    // ---------------------------------------------------------------------------------- Ops console

    [Fact]
    public async Task OperationsReadsEachJobsLastSuccessAndTheRunHistory()
    {
        var jobs = Registry();
        var job = jobs.Define(Name("history"), TimeSpan.FromHours(1));
        await jobs.RunAsync(job, _ => Task.FromResult(JobResult.Success(Counts(("deleted", 7)))), default);
        await jobs.RunAsync(job, _ => throw new ArgumentException("row data"), default);
        var ops = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), Role.OPS, Scope.Platform, false);

        var list = await api.Client.SendAsync(api.As(ops, HttpMethod.Get, "/api/v1/ops/jobs"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var health = await list.JsonAsync();
        var names = health.EnumerateArray().Select(j => j.GetProperty("job").GetString()!).ToList();
        Assert.Equal(Services.GetRequiredService<Jobs>().Defined().Select(j => j.Name).Order(), names.Order());
        Assert.Superset(PlatformJobs.ToHashSet(), names.ToHashSet());
        var runRetention = health.EnumerateArray().Single(j => j.GetProperty("job").GetString() == "job-run-retention");
        Assert.Equal("PT26H", runRetention.GetProperty("maxAge").GetString());
        Assert.Equal("PENDING", runRetention.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, runRetention.GetProperty("lastSuccessCounts").ValueKind);

        var runs = await api.Client.SendAsync(api.As(ops, HttpMethod.Get, $"/api/v1/ops/jobs/runs?job={job.Name}"));
        var body = await runs.Content.ReadAsStringAsync();
        Assert.True(runs.StatusCode == HttpStatusCode.OK, body);
        var items = (await runs.JsonAsync()).GetProperty("items");
        Assert.Equal(new[] { "FAILED", "SUCCESS" }, items.EnumerateArray().Select(i => i.GetProperty("outcome").GetString()!).ToArray());
        Assert.Equal(7, items[1].GetProperty("counts").GetProperty("deleted").GetInt64());
        Assert.Equal("System.ArgumentException", items[0].GetProperty("errorClass").GetString());
        Assert.EndsWith("Z", items[0].GetProperty("startedAt").GetString());
        Assert.DoesNotContain("row data", body);

        var paged = await (await api.Client.SendAsync(api.As(ops, HttpMethod.Get, $"/api/v1/ops/jobs/runs?job={job.Name}&limit=1"))).JsonAsync();
        Assert.Equal(1, paged.GetProperty("items").GetArrayLength());
        var cursor = paged.GetProperty("nextCursor").GetString();
        var next = await (await api.Client.SendAsync(api.As(ops, HttpMethod.Get, $"/api/v1/ops/jobs/runs?job={job.Name}&limit=1&cursor={cursor}"))).JsonAsync();
        Assert.Equal("SUCCESS", next.GetProperty("items")[0].GetProperty("outcome").GetString());

        var bad = await Http.AssertErrorAsync(await api.Client.SendAsync(api.As(ops, HttpMethod.Get, "/api/v1/ops/jobs/runs?job=Not%20A%20Job")),
            400, "VALIDATION_FAILED");
        Assert.Equal("job", bad.GetProperty("field").GetString());
        await Http.AssertErrorAsync(await api.Client.SendAsync(api.As(ops, HttpMethod.Get, "/api/v1/ops/jobs/runs?cursor=nope")), 400, "INVALID_CURSOR");

        var company = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), Role.FLEET_MANAGER, Scope.Company(Guid.CreateVersion7()), false);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.SendAsync(api.As(company, HttpMethod.Get, "/api/v1/ops/jobs"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.SendAsync(api.As(company, HttpMethod.Get, "/api/v1/ops/jobs/runs"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAsync("/api/v1/ops/jobs")).StatusCode);
    }

    // ---------------------------------------------------------------------------------------- helpers

    private async Task<Guid> PlantRun(string job, string startedAt, string outcome, string? errorClass)
    {
        var id = Guid.NewGuid();
        await Owner.ExecuteAsync($"""
            insert into job_runs (id, job_name, instance_id, started_at, finished_at, outcome, counts, error_class)
            values (@id, @job, 'test@host', {startedAt}, {startedAt} + interval '1 second', @outcome, '{"{}"}'::jsonb, @errorClass)
            """, new { id, job, outcome, errorClass });
        return id;
    }

    private static string Record(string job, string finished, string outcomeCountsError) =>
        $"select job_run_record(gen_random_uuid(), '{job}', 'test@host', now() - interval '1 second', {finished}, {outcomeCountsError})";

    private async Task AssertRefused(string outcomeCountsError, string job, string finished, string what)
    {
        // check_violation: a table check, or the function's own refusal of a future finish.
        var refusal = await RuntimeConnections.RefusalAsync(Owner, null, Record(job, finished, outcomeCountsError));
        Assert.True(refusal?.SqlState == "23514", $"{what}: {refusal?.SqlState} {refusal?.MessageText}");
    }

    private async Task<long> VisibleRuns(string? scope, string job)
    {
        var (connection, tx) = await RuntimeConnections.OpenAsync(Owner, scope);
        await using (connection)
        await using (tx)
        {
            await using var count = new NpgsqlCommand("select count(*) from job_runs where job_name = @job", connection, tx);
            count.Parameters.AddWithValue("job", job);
            return (long)(await count.ExecuteScalarAsync())!;
        }
    }

    /// <summary>A database that cannot be reached: every transaction fails to open.</summary>
    private sealed class DatabaseDown : ITransactions
    {
        public Task<DbTx> BeginAsync(DbScopeContext scope, CancellationToken ct = default) =>
            throw new NpgsqlException("the database is down");

        public Task<T> RunAsync<T>(DbScopeContext scope, Func<DbTx, Task<T>> work, bool retryOnConflict = false, CancellationToken ct = default) =>
            throw new NpgsqlException("the database is down");
    }
}
