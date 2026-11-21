using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Platform.Jobs;

/// <summary>
/// Scheduled jobs in the Ops console (ADR 0028, runbook "Reading job health"; Java <c>OpsJobsController</c>): the
/// last success of each job against its window, and the run history. Platform roles only; nothing here is tenant data.
/// </summary>
[Route(ApiContract.BasePath + "/ops/jobs"), Tags("platform")]
public sealed class OpsJobsController(IJobsHealth health, IDbSession db) : ApiControllerBase
{
    /// <summary>Each scheduled job: its last success, against its window</summary>
    [HttpGet(Name = "listJobHealth"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("Each scheduled job: its last success, against its window")]
    [ProducesResponseType<List<JobHealthResponse>>(200)]
    public async Task<IActionResult> ListJobHealth(CancellationToken ct) =>
        From(Result<List<JobHealthResponse>>.Ok((await health.StatusesAsync(ct)).Select(JobHealthResponse.Of).ToList()));

    /// <summary>Job runs, newest first; optionally of one job</summary>
    [HttpGet("runs", Name = "listJobRuns"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("Job runs, newest first; optionally of one job")]
    [ProducesResponseType<Page<JobRunResponse>>(200)]
    public async Task<IActionResult> ListJobRuns([FromQuery] string? job, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        if (job is not null && !Jobs.IsName(job))
        {
            return From(Result.Fail(400, ErrorCodes.ValidationFailed, "Not a job name", "job"));
        }
        var size = Page<JobRunResponse>.Limit(limit);
        var after = Cursors.Parse(cursor);
        // On the request's transaction, so the platform scope reaches row-level security on job_runs.
        var rows = await JobRunStore.RunsAsync(db.Current, job, after, size + 1, ct);
        var page = Page<JobRunStore.Run>.Of(rows, size, r => r.Id);
        return From(Result<Page<JobRunResponse>>.Ok(new Page<JobRunResponse>(page.Items.Select(JobRunResponse.Of).ToList(), page.NextCursor)));
    }
}

/// <param name="State">OK; PENDING (not run yet since start, still in its window); FAILING (latest attempt failed, last
/// success still in its window); OVERDUE (no success within its window)</param>
/// <param name="MaxAge">How long the job may go without a success: an ISO-8601 duration such as PT27H</param>
/// <param name="DueBy">When the job becomes OVERDUE without another success</param>
/// <param name="LastSuccessCounts">The last success's counts: numbers only</param>
/// <param name="LastFailureErrorClass">The last failure's exception class; never its message</param>
public sealed record JobHealthResponse(string Job, JobState State, string MaxAge, DateTimeOffset DueBy, DateTimeOffset? LastStartedAt,
    JobOutcome? LastOutcome, DateTimeOffset? LastSuccessAt, IReadOnlyDictionary<string, long>? LastSuccessCounts, DateTimeOffset? LastFailureAt,
    string? LastFailureErrorClass)
{
    public static JobHealthResponse Of(JobStatus s) => new(s.Job.Name, s.State, Durations.ToJava(s.Job.MaxAge), s.DueBy, s.LastStartedAt,
        s.LastOutcome, s.LastSuccessAt, s.LastSuccessCounts, s.LastFailureAt, s.LastFailureErrorClass);
}

/// <param name="Instance">pid@host of the instance</param>
/// <param name="Counts">Numbers only</param>
/// <param name="ErrorClass">FAILED only: the exception class, never its message</param>
public sealed record JobRunResponse(Guid Id, string Job, string Instance, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, JobOutcome Outcome,
    IReadOnlyDictionary<string, long> Counts, string? ErrorClass)
{
    public static JobRunResponse Of(JobRunStore.Run r) => new(r.Id, r.Job, r.Instance, r.StartedAt, r.FinishedAt, r.Outcome, r.Counts, r.ErrorClass);
}
