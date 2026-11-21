using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Platform.Jobs;

public enum JobOutcome
{
    SUCCESS,
    /// <summary>The body threw; only the exception's class name is recorded.</summary>
    FAILED,
    /// <summary>The job's own lock or lease said another instance runs it, or already ran it.</summary>
    SKIPPED_LOCKED,
}

/// <summary>
/// What one run of a job came to. Counts are numbers only, under the fixed names of V21's
/// <c>job_run_count_keys()</c>: never a value, a name or a message (Java <c>JobResult</c>).
/// </summary>
public sealed record JobResult
{
    public static readonly IReadOnlySet<string> CountKeys = new HashSet<string>
    {
        "columnErrors", "deleted", "failed", "findings", "mfaChallenges", "payables", "reencrypted", "remaining", "sessions", "wallets",
    };

    public JobResult(JobOutcome outcome, IReadOnlyDictionary<string, long> counts)
    {
        foreach (var (key, value) in counts)
        {
            if (!CountKeys.Contains(key) || value < 0)
            {
                throw new ArgumentException("A job count is a non-negative number under a listed name");
            }
        }
        Outcome = outcome;
        Counts = new Dictionary<string, long>(counts);
    }

    public JobOutcome Outcome { get; }
    public IReadOnlyDictionary<string, long> Counts { get; }

    public static JobResult Success(IReadOnlyDictionary<string, long> counts) => new(JobOutcome.SUCCESS, counts);
    public static JobResult SkippedLocked() => new(JobOutcome.SKIPPED_LOCKED, new Dictionary<string, long>());
    internal static JobResult Failed() => new(JobOutcome.FAILED, new Dictionary<string, long>());
}

/// <summary>A declared job: its name and how long it may go without a success before health says DOWN.</summary>
public sealed record Job(string Name, TimeSpan MaxAge);

/// <summary>
/// Runs a scheduled job and leaves a record of it in <c>job_runs</c> (V21, ADR 0028; Java <c>Jobs</c>).
/// The job's body keeps its own lock or lease; this adds none. The record is written after the body, in a
/// transaction of its own, through the <c>job_run_record</c> SECURITY DEFINER function. A failure is
/// caught, logged and recorded with its class name only (a PostgreSQL error quotes the failing row). If
/// the record cannot be written, the job is unaffected.
/// </summary>
public sealed partial class Jobs(ITransactions transactions, TimeProvider clock, IConfiguration configuration, ILogger<Jobs> logger)
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,62}$")]
    private static partial Regex Name();

    private readonly ConcurrentDictionary<string, Job> defined = new();
    private readonly string instance = InstanceId($"{Environment.ProcessId}@{Environment.MachineName}");

    public DateTimeOffset StartedAt { get; } = clock.GetUtcNow();

    /// <summary>
    /// Declares a job, so the health check expects it before its first run. <c>Zawed:Jobs:MaxAge:{name}</c>
    /// overrides the window (the Java <c>zawed.jobs.max-age.&lt;name&gt;</c>).
    /// </summary>
    public Job Define(string name, TimeSpan defaultMaxAge)
    {
        if (!Name().IsMatch(name))
        {
            throw new ArgumentException($"Job name {name} is not lower-case words joined by dashes");
        }
        var maxAge = configuration.GetValue($"Zawed:Jobs:MaxAge:{name}", defaultMaxAge);
        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentException($"Zawed:Jobs:MaxAge:{name} must be positive");
        }
        var job = new Job(name, maxAge);
        if (!defined.TryAdd(name, job))
        {
            throw new InvalidOperationException($"Job {name} is defined twice");
        }
        return job;
    }

    public IReadOnlyList<Job> Defined() => defined.Values.OrderBy(j => j.Name, StringComparer.Ordinal).ToList();

    /// <summary>Runs the body, records the run, and returns what came of it. Never throws for a failed body.</summary>
    public async Task<JobResult> RunAsync(Job job, Func<CancellationToken, Task<JobResult>> body, CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        JobResult result;
        string? errorClass = null;
        try
        {
            result = await body(ct) ?? throw new InvalidOperationException("a job body returns its result");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Job {Job} failed", job.Name);
            result = JobResult.Failed();
            errorClass = ErrorClass(ex);
        }
        var finished = clock.GetUtcNow();
        try
        {
            await transactions.RunAsync(DbScopeContext.None, tx => tx.ExecuteAsync(
                "select job_run_record(@id, @job, @instance, @started, @finished, @outcome, cast(@counts as jsonb), @error)",
                new
                {
                    id = Guid.CreateVersion7(started),
                    job = job.Name,
                    instance,
                    started = started.UtcDateTime,
                    finished = (finished < started ? started : finished).UtcDateTime,
                    outcome = result.Outcome.ToString(),
                    counts = JsonSerializer.Serialize(result.Counts),
                    error = errorClass,
                }), ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Job {Job} ({Outcome}) could not be recorded: {Error}", job.Name, result.Outcome, ex.GetType().FullName);
        }
        return result;
    }

    /// <summary>The exception's type name, reduced to what <c>job_runs.error_class</c> accepts. Never its message.</summary>
    public static string ErrorClass(Exception ex)
    {
        var name = Regex.Replace(ex.GetType().FullName ?? ex.GetType().Name, "[^A-Za-z0-9_$.]", "_");
        if (!char.IsLetter(name[0]) && name[0] != '_' && name[0] != '$')
        {
            name = "_" + name;
        }
        return name.Length > 200 ? name[..200] : name;
    }

    /// <summary>"pid@host", reduced to what <c>job_runs.instance_id</c> accepts.</summary>
    public static string InstanceId(string raw)
    {
        var cleaned = Regex.Replace(raw ?? "", "[^A-Za-z0-9@._:-]", "-");
        return cleaned.Length == 0 ? "unknown" : cleaned.Length > 128 ? cleaned[..128] : cleaned;
    }
}

/// <summary>
/// A periodic job (the Java <c>@Scheduled(fixedDelay, initialDelay)</c>): a <see cref="BackgroundService"/>
/// with a <see cref="PeriodicTimer"/>, no extra infrastructure (ADR 0011, ADR 0029). Each tick goes
/// through <see cref="Jobs.RunAsync"/>. Several instances may run it at once: the body takes its own
/// lock where only one may work (an advisory lock), or simply shares the work (the purges).
/// </summary>
public abstract class ScheduledJob(Jobs jobs, string name, TimeSpan maxAge) : BackgroundService
{
    protected Job Job { get; } = jobs.Define(name, maxAge);

    protected abstract TimeSpan InitialDelay { get; }

    protected abstract TimeSpan Interval { get; }

    protected abstract Task<JobResult> RunOnceAsync(CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await jobs.RunAsync(Job, RunOnceAsync, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown: the current run finished or was cancelled with the host.
        }
    }
}
