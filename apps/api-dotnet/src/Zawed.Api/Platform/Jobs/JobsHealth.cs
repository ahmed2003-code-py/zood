using System.Text.Json;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Platform.Jobs;

public enum JobState
{
    /// <summary>Succeeded within its window, and nothing failed since.</summary>
    OK,
    /// <summary>Has not run yet since this instance started, and its window from the start is still open.</summary>
    PENDING,
    /// <summary>Its latest attempt failed, but a success is still within the window.</summary>
    FAILING,
    /// <summary>No success within the window: the job is not doing its work.</summary>
    OVERDUE,
}

/// <summary>One job's state now, with its latest run, success and failure (Java <c>JobsHealth.Status</c>).</summary>
public sealed record JobStatus(Job Job, JobState State, DateTimeOffset DueBy, DateTimeOffset? LastStartedAt, JobOutcome? LastOutcome,
    DateTimeOffset? LastSuccessAt, IReadOnlyDictionary<string, long>? LastSuccessCounts, DateTimeOffset? LastFailureAt,
    string? LastFailureErrorClass);

/// <summary>"Last success per job", for the health check and the Ops console (a seam the health tests replace).</summary>
public interface IJobsHealth
{
    /// <summary>Every job this application declares, by name, with its state now.</summary>
    Task<IReadOnlyList<JobStatus>> StatusesAsync(CancellationToken ct);
}

/// <summary>
/// "Last success per job" (ADR 0028; Java <c>JobsHealth</c>), for the health check and the Ops console. A job is
/// judged against its own <see cref="Job.MaxAge"/>: the time it may go without a success, set well above its schedule
/// (reconciliation runs nightly and may go 27 hours).
/// </summary>
public sealed class JobsHealth(Jobs jobs, ITransactions transactions, TimeProvider clock) : IJobsHealth
{
    public async Task<IReadOnlyList<JobStatus>> StatusesAsync(CancellationToken ct)
    {
        var defined = jobs.Defined();
        if (defined.Count == 0)
        {
            return [];
        }
        var latest = (await transactions.RunAsync(DbScopeContext.None, tx => JobRunStore.LatestAsync(tx, defined.Select(j => j.Name), ct), ct: ct))
            .ToDictionary(l => l.Job);
        var now = clock.GetUtcNow();
        return defined.Select(job =>
        {
            latest.TryGetValue(job.Name, out var row);
            var dueBy = DueBy(job.MaxAge, row?.LastSuccessAt, jobs.StartedAt);
            return new JobStatus(job, Evaluate(dueBy, row?.LastSuccessAt, row?.LastFailureAt, now), dueBy, row?.LastStartedAt,
                row?.LastOutcome, row?.LastSuccessAt, row?.LastSuccessCounts, row?.LastFailureAt, row?.LastFailureErrorClass);
        }).ToList();
    }

    /// <summary>
    /// The latest a next success may come: the window counts from the last success, or, for a job that has never
    /// succeeded, from this instance's start (a new deploy is not late yet).
    /// </summary>
    public static DateTimeOffset DueBy(TimeSpan maxAge, DateTimeOffset? lastSuccess, DateTimeOffset instanceStarted) =>
        (lastSuccess ?? instanceStarted) + maxAge;

    public static JobState Evaluate(DateTimeOffset dueBy, DateTimeOffset? lastSuccess, DateTimeOffset? lastFailure, DateTimeOffset now)
    {
        if (now > dueBy)
        {
            return JobState.OVERDUE;
        }
        if (lastFailure is { } f && (lastSuccess is null || f > lastSuccess))
        {
            return JobState.FAILING;
        }
        return lastSuccess is null ? JobState.PENDING : JobState.OK;
    }
}

/// <summary>
/// The SQL behind <c>job_runs</c> (V21; Java <c>JobRunStore</c>). The health summary goes through the table's
/// SECURITY DEFINER function, because the health check has no principal; the run history is read from the table
/// itself, under row-level security (platform scope only).
/// </summary>
public static class JobRunStore
{
    /// <summary>The latest run, success and failure of each job named; a job that never ran has nulls.</summary>
    public sealed record Latest(string Job, DateTimeOffset? LastStartedAt, DateTimeOffset? LastFinishedAt, JobOutcome? LastOutcome,
        DateTimeOffset? LastSuccessAt, IReadOnlyDictionary<string, long>? LastSuccessCounts, DateTimeOffset? LastFailureAt,
        string? LastFailureErrorClass);

    public sealed record Run(Guid Id, string Job, string Instance, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, JobOutcome Outcome,
        IReadOnlyDictionary<string, long> Counts, string? ErrorClass);

    private sealed record LatestRow(string job_name, DateTime? last_started_at, DateTime? last_finished_at, string? last_outcome,
        DateTime? last_success_at, string? last_success_counts, DateTime? last_failure_at, string? last_failure_error_class);

    private sealed record RunRow(Guid id, string job_name, string instance_id, DateTime started_at, DateTime finished_at, string outcome,
        string counts, string? error_class);

    public static async Task<IReadOnlyList<Latest>> LatestAsync(DbTx tx, IEnumerable<string> jobs, CancellationToken ct)
    {
        // Job names never contain a comma (V21's name check), so one text parameter carries the list.
        var rows = await tx.QueryAsync<LatestRow>("""
            select job_name, last_started_at, last_finished_at, last_outcome, last_success_at, last_success_counts::text as last_success_counts,
                   last_failure_at, last_failure_error_class
            from job_run_latest(string_to_array(@names, ','))
            """, new { names = string.Join(',', jobs) }, ct);
        return rows.Select(r => new Latest(r.job_name, Utc(r.last_started_at), Utc(r.last_finished_at), Outcome(r.last_outcome),
            Utc(r.last_success_at), Counts(r.last_success_counts), Utc(r.last_failure_at), r.last_failure_error_class)).ToList();
    }

    /// <summary>Newest first; <paramref name="job"/> null for every job. Platform scope only (RLS).</summary>
    public static async Task<IReadOnlyList<Run>> RunsAsync(DbTx tx, string? job, Guid? cursor, int limit, CancellationToken ct)
    {
        var rows = await tx.QueryAsync<RunRow>("""
            select id, job_name, instance_id, started_at, finished_at, outcome, counts::text as counts, error_class from job_runs
            where (cast(@job as text) is null or job_name = @job)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new { job, cursor, limit }, ct);
        return rows.Select(r => new Run(r.id, r.job_name, r.instance_id, Utc(r.started_at)!.Value, Utc(r.finished_at)!.Value,
            Outcome(r.outcome)!.Value, Counts(r.counts)!, r.error_class)).ToList();
    }

    /// <returns>how many runs were deleted</returns>
    public static Task<int> PurgeAsync(DbTx tx, DateTimeOffset before, int limit, CancellationToken ct) =>
        tx.QuerySingleAsync<int>("select purge_job_runs(@before, @limit)", new { before = before.UtcDateTime, limit }, ct);

    private static DateTimeOffset? Utc(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private static JobOutcome? Outcome(string? value) => value is null ? null : Enum.Parse<JobOutcome>(value);

    private static Dictionary<string, long>? Counts(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<Dictionary<string, long>>(json);
}
