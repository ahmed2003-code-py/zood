using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Platform.Jobs;

/// <summary>
/// Keeps <c>job_runs</c> itself bounded (V21, ADR 0028; Java <c>JobRunRetention</c>): runs that started more than
/// <c>Zawed:Jobs:RunRetention</c> ago (90 days by default; never under <see cref="Minimum"/>) are deleted through
/// <c>purge_job_runs</c>, which refuses anything inside <see cref="DatabaseFloor"/>. It is a job like the others, and
/// so is recorded too.
/// </summary>
public sealed class JobRunRetention : ScheduledJob
{
    public static readonly TimeSpan DatabaseFloor = TimeSpan.FromDays(30);

    /// <summary>The floor plus a day, so a clock ahead of the database's never trips the function's floor.</summary>
    public static readonly TimeSpan Minimum = DatabaseFloor + TimeSpan.FromDays(1);

    public const int Batch = 5_000;

    public const int MaxBatchesPerRun = 20;

    private readonly ITransactions transactions;
    private readonly TimeProvider clock;
    private readonly ILogger<JobRunRetention> logger;
    private readonly TimeSpan retention;

    public JobRunRetention(Jobs jobs, ITransactions transactions, TimeProvider clock, IConfiguration configuration, ILogger<JobRunRetention> logger)
        : base(jobs, "job-run-retention", TimeSpan.FromHours(26))
    {
        this.transactions = transactions;
        this.clock = clock;
        this.logger = logger;
        var configured = Durations.Get(configuration, "Zawed:Jobs:RunRetention", TimeSpan.FromDays(90));
        retention = configured < Minimum ? Minimum : configured;
        Interval = Durations.Get(configuration, "Zawed:Jobs:RunPurgeInterval", TimeSpan.FromHours(6));
    }

    protected override TimeSpan InitialDelay => TimeSpan.FromMinutes(10);

    protected override TimeSpan Interval { get; }

    protected override async Task<JobResult> RunOnceAsync(CancellationToken ct) =>
        JobResult.Success(new Dictionary<string, long> { ["deleted"] = await PurgeAsync(ct) });

    /// <returns>how many runs were deleted</returns>
    public async Task<long> PurgeAsync(CancellationToken ct = default)
    {
        var before = clock.GetUtcNow() - retention;
        long total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var deleted = await transactions.RunAsync(DbScopeContext.None, tx => JobRunStore.PurgeAsync(tx, before, Batch, ct), ct: ct);
            total += deleted;
            if (deleted < Batch)
            {
                break;
            }
        }
        if (total > 0)
        {
            logger.LogInformation("Purged {Count} job runs older than {Before}", total, before);
        }
        return total;
    }
}
