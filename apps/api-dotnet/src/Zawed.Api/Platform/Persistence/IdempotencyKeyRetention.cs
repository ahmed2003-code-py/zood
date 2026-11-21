using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Jobs;

namespace Zawed.Api.Platform.Persistence;

/// <summary>
/// Keeps <c>idempotency_keys</c> bounded (V17, ADR 0022; Java <c>IdempotencyKeyRetention</c>). A key only has to
/// outlive every retry of the request it was sent with. Keys older than <c>Zawed:Idempotency:KeyRetention</c> (30 days
/// by default, never under <see cref="Minimum"/>) are deleted in bounded batches, each its own short transaction. Two
/// instances running it at once only share the work.
/// <para>After the purge, a request sent again with a purged key is a new request: that is what the retention means,
/// and why it is far longer than any retry.</para>
/// <para>The deletion is <c>purge_idempotency_keys</c>, a bounded SECURITY DEFINER function that refuses a cutoff
/// inside <see cref="DatabaseFloor"/>. The runtime role has no DELETE on the table and the job needs no scope.</para>
/// </summary>
public sealed class IdempotencyKeyRetention : ScheduledJob
{
    /// <summary>What <c>purge_idempotency_keys</c> refuses to go under, whoever calls it.</summary>
    public static readonly TimeSpan DatabaseFloor = TimeSpan.FromDays(7);

    /// <summary>The floor plus a day, so a clock ahead of the database's never makes the function refuse the cutoff.</summary>
    public static readonly TimeSpan Minimum = DatabaseFloor + TimeSpan.FromDays(1);

    public const int Batch = 5_000;

    /// <summary>A run stops after this many batches and finishes on the next run.</summary>
    public const int MaxBatchesPerRun = 40;

    private readonly ITransactions transactions;
    private readonly TimeProvider clock;
    private readonly ILogger<IdempotencyKeyRetention> logger;
    private readonly TimeSpan retention;

    public IdempotencyKeyRetention(Jobs.Jobs jobs, ITransactions transactions, TimeProvider clock, IConfiguration configuration,
        ILogger<IdempotencyKeyRetention> logger)
        : base(jobs, "idempotency-key-retention", TimeSpan.FromHours(14))
    {
        this.transactions = transactions;
        this.clock = clock;
        this.logger = logger;
        retention = Effective(Durations.Get(configuration, "Zawed:Idempotency:KeyRetention", TimeSpan.FromDays(30)));
        Interval = Durations.Get(configuration, "Zawed:Idempotency:PurgeInterval", TimeSpan.FromHours(6));
    }

    public static TimeSpan Effective(TimeSpan configured) => configured < Minimum ? Minimum : configured;

    protected override TimeSpan InitialDelay => TimeSpan.FromMinutes(5);

    protected override TimeSpan Interval { get; }

    protected override async Task<JobResult> RunOnceAsync(CancellationToken ct) =>
        JobResult.Success(new Dictionary<string, long> { ["deleted"] = await PurgeAsync(ct) });

    /// <returns>how many keys were deleted</returns>
    public async Task<long> PurgeAsync(CancellationToken ct = default)
    {
        var before = clock.GetUtcNow() - retention;
        long total = 0;
        var finished = false;
        for (var batch = 0; batch < MaxBatchesPerRun && !finished; batch++)
        {
            var deleted = await transactions.RunAsync(DbScopeContext.None, tx => tx.QuerySingleAsync<int>(
                "select purge_idempotency_keys(@before, @limit)", new { before = before.UtcDateTime, limit = Batch }, ct), ct: ct);
            total += deleted;
            finished = deleted < Batch;
        }
        if (!finished)
        {
            logger.LogWarning("Idempotency key purge stopped after {Batches} batches; the rest is deleted on the next run", MaxBatchesPerRun);
        }
        if (total > 0)
        {
            logger.LogInformation("Purged {Count} idempotency keys older than {Before}", total, before);
        }
        return total;
    }
}
