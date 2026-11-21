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

public sealed record JobStatus(Job Job, JobState State, DateTimeOffset DueBy, DateTime? LastStartedAt, string? LastOutcome,
    DateTime? LastSuccessAt, DateTime? LastFailureAt, string? LastFailureErrorClass);

/// <summary>"Last success per job" (ADR 0028; Java <c>JobsHealth</c>), for the health check and the Ops console.</summary>
public sealed class JobsHealth(Jobs jobs, ITransactions transactions, TimeProvider clock)
{
    private sealed record Latest(string job_name, DateTime? last_started_at, string? last_outcome, DateTime? last_success_at,
        DateTime? last_failure_at, string? last_failure_error_class);

    public async Task<IReadOnlyList<JobStatus>> StatusesAsync(CancellationToken ct)
    {
        var defined = jobs.Defined();
        if (defined.Count == 0)
        {
            return [];
        }
        // Job names never contain a comma (V21's name check), so one text parameter carries the list.
        var latest = (await transactions.RunAsync(DbScopeContext.None, tx => tx.QueryAsync<Latest>(
                "select job_name, last_started_at, last_outcome, last_success_at, last_failure_at, last_failure_error_class "
                + "from job_run_latest(string_to_array(@names, ','))",
                new { names = string.Join(',', defined.Select(j => j.Name)) }, ct), ct: ct))
            .ToDictionary(l => l.job_name);
        var now = clock.GetUtcNow();
        return defined.Select(job =>
        {
            latest.TryGetValue(job.Name, out var row);
            var dueBy = DueBy(job.MaxAge, row?.last_success_at, jobs.StartedAt);
            return new JobStatus(job, Evaluate(dueBy, row?.last_success_at, row?.last_failure_at, now), dueBy, row?.last_started_at,
                row?.last_outcome, row?.last_success_at, row?.last_failure_at, row?.last_failure_error_class);
        }).ToList();
    }

    /// <summary>The window counts from the last success, or, for a job that never succeeded, from this instance's start.</summary>
    public static DateTimeOffset DueBy(TimeSpan maxAge, DateTime? lastSuccess, DateTimeOffset instanceStarted) =>
        (lastSuccess is { } s ? new DateTimeOffset(DateTime.SpecifyKind(s, DateTimeKind.Utc)) : instanceStarted) + maxAge;

    public static JobState Evaluate(DateTimeOffset dueBy, DateTime? lastSuccess, DateTime? lastFailure, DateTimeOffset now)
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
