using Zawed.Api.Platform.Jobs;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Modules.Network;

/// <summary>
/// Keeps <c>device_nonces</c> bounded (audit F5, V14; Java <c>network.NonceRetention</c>). A device signs a fresh nonce for
/// every token, at most every 15 minutes while it is in use: a terminal in use around the clock writes up to 96 a day.
/// <para>A token request is accepted only within ±5 minutes of its own timestamp, so a nonce can be replayed for at most
/// 10 minutes; nonces older than <c>Zawed:Pos:NonceRetention</c> (one hour by default, never under 15 minutes, which the
/// database function enforces too) are deleted in bounded batches, each its own short transaction. Two instances running
/// it at once only share the work. Each run is recorded in <c>job_runs</c>.</para>
/// </summary>
public sealed class NonceRetention : ScheduledJob
{
    public const int Batch = 5_000;

    /// <summary>A run stops after this many batches and finishes on the next run.</summary>
    public const int MaxBatchesPerRun = 40;

    private readonly Jobs jobs;
    private readonly ITransactions transactions;
    private readonly TimeProvider clock;
    private readonly ILogger<NonceRetention> logger;
    private readonly TimeSpan retention;
    private readonly TimeSpan interval;

    public NonceRetention(Jobs jobs, ITransactions transactions, TimeProvider clock, IConfiguration configuration, ILogger<NonceRetention> logger)
        : base(jobs, "nonce-retention", TimeSpan.FromHours(1))
    {
        this.jobs = jobs;
        this.transactions = transactions;
        this.clock = clock;
        this.logger = logger;
        var configured = configuration.GetValue("Zawed:Pos:NonceRetention", TimeSpan.FromHours(1));
        retention = configured < TimeSpan.FromMinutes(15) ? TimeSpan.FromMinutes(15) : configured;
        interval = configuration.GetValue("Zawed:Pos:NoncePurgeInterval", TimeSpan.FromMinutes(15));
    }

    protected override TimeSpan InitialDelay => TimeSpan.FromMinutes(2);

    protected override TimeSpan Interval => interval;

    /// <summary>One run, recorded in <c>job_runs</c>, as the timer runs it (the Java <c>scheduled()</c>).</summary>
    public Task<JobResult> RunNowAsync(CancellationToken ct = default) => jobs.RunAsync(Job, RunOnceAsync, ct);

    protected override async Task<JobResult> RunOnceAsync(CancellationToken ct) =>
        JobResult.Success(new Dictionary<string, long> { ["deleted"] = await PurgeAsync(ct) });

    /// <summary>How many nonces were deleted.</summary>
    public async Task<long> PurgeAsync(CancellationToken ct = default)
    {
        var before = clock.GetUtcNow() - retention;
        long total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var deleted = await transactions.RunAsync(DbScopeContext.None,
                tx => tx.QuerySingleAsync<int?>("select purge_device_nonces(@before, @limit)", new { before, limit = Batch }, ct), ct: ct);
            total += deleted ?? 0;
            if (deleted is null || deleted < Batch)
            {
                break;
            }
        }
        if (total > 0)
        {
            logger.LogInformation("Purged {Count} device nonces older than {Before}", total, before);
        }
        return total;
    }
}
