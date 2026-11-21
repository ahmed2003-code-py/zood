using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Jobs;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Modules.Leads;

/// <summary>
/// Deletes closed leads after their retention (u1-leads-contract.md §6; Java <c>LeadRetention</c>):
/// <c>Zawed:Leads:ClosedRetention</c>, 24 months after closing by default. Open leads are kept: they are Operations' work
/// in progress. The deletion is <c>purge_closed_leads</c> (V22), a bounded SECURITY DEFINER function that refuses a cutoff
/// inside <see cref="DatabaseFloor"/>; the runtime role has no DELETE on <c>leads</c>. In bounded batches, each its own
/// transaction, like <see cref="IdempotencyKeyRetention"/>. Daily.
/// </summary>
public sealed class LeadRetention : ScheduledJob
{
    /// <summary>What <c>purge_closed_leads</c> refuses to go under, whoever calls it.</summary>
    public static readonly TimeSpan DatabaseFloor = TimeSpan.FromDays(90);

    /// <summary>The shortest retention the job asks for: a day over the floor, for a clock ahead of the database's.</summary>
    public static readonly TimeSpan Minimum = DatabaseFloor + TimeSpan.FromDays(1);

    public const int Batch = 1_000;
    public const int MaxBatchesPerRun = 20;

    private readonly ITransactions transactions;
    private readonly TimeProvider clock;
    private readonly ILogger<LeadRetention> logger;
    private readonly TimeSpan retention;

    public LeadRetention(Jobs jobs, ITransactions transactions, TimeProvider clock, IConfiguration configuration, ILogger<LeadRetention> logger)
        // Daily: two runs a day apart, plus margin for a restart (ADR 0028).
        : base(jobs, "lead-retention", TimeSpan.FromHours(50))
    {
        this.transactions = transactions;
        this.clock = clock;
        this.logger = logger;
        retention = Effective(Durations.Get(configuration, "Zawed:Leads:ClosedRetention", TimeSpan.FromDays(730)));
        Interval = Durations.Get(configuration, "Zawed:Leads:PurgeInterval", TimeSpan.FromHours(24));
    }

    public static TimeSpan Effective(TimeSpan configured) => configured < Minimum ? Minimum : configured;

    protected override TimeSpan InitialDelay => TimeSpan.FromMinutes(10);

    protected override TimeSpan Interval { get; }

    protected override async Task<JobResult> RunOnceAsync(CancellationToken ct) =>
        JobResult.Success(new Dictionary<string, long> { ["deleted"] = await PurgeAsync(ct) });

    /// <returns>how many closed leads were deleted</returns>
    public async Task<long> PurgeAsync(CancellationToken ct = default)
    {
        var before = clock.GetUtcNow() - retention;
        long total = 0;
        var finished = false;
        for (var batch = 0; batch < MaxBatchesPerRun && !finished; batch++)
        {
            var deleted = await transactions.RunAsync(DbScopeContext.None, tx => tx.QuerySingleAsync<int?>(
                "select purge_closed_leads(@before, @limit)", new { before = before.UtcDateTime, limit = Batch }, ct), ct: ct);
            total += deleted ?? 0;
            finished = deleted is null || deleted < Batch;
        }
        if (total > 0)
        {
            logger.LogInformation("Purged {Count} leads closed before {Before}", total, before);
        }
        return total;
    }
}
