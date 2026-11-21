using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Jobs;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Modules.Notifications;

/// <summary>
/// Keeps <c>notifications</c> bounded (B7 W5; Java <c>notifications.NotificationRetention</c>): notifications older than
/// <c>Zawed:Notifications:Retention</c> (Java <c>zawed.notifications.retention</c>, 90 days by default, never under 30,
/// which the database function enforces too) are deleted with their read rows, in bounded batches, each its own short
/// transaction.
/// <para>The delete goes through <c>purge_notifications</c>, a narrow SECURITY DEFINER function, rather than a
/// platform-scope escalation: <c>zawed_app</c> then holds no <c>DELETE</c> on either table at all, so no request in any
/// scope can remove a notification. Each run is recorded in <c>job_runs</c>.</para>
/// </summary>
public sealed class NotificationRetention : ScheduledJob
{
    public const int Batch = 5_000;

    /// <summary>A run stops after this many batches and finishes on the next run.</summary>
    public const int MaxBatchesPerRun = 40;

    public static readonly TimeSpan Minimum = TimeSpan.FromDays(30);

    private readonly ITransactions transactions;
    private readonly TimeProvider clock;
    private readonly ILogger<NotificationRetention> logger;
    private readonly TimeSpan retention;
    private readonly TimeSpan interval;

    // Every 6 hours: two missed runs and a little more.
    public NotificationRetention(Jobs jobs, ITransactions transactions, TimeProvider clock, IConfiguration configuration,
        ILogger<NotificationRetention> logger)
        : base(jobs, "notification-retention", TimeSpan.FromHours(14))
    {
        this.transactions = transactions;
        this.clock = clock;
        this.logger = logger;
        // Java zawed.notifications.retention and zawed.notifications.purge-interval.
        var configured = Durations.Get(configuration, "Zawed:Notifications:Retention", TimeSpan.FromDays(90));
        retention = configured < Minimum ? Minimum : configured;
        interval = Durations.Get(configuration, "Zawed:Notifications:PurgeInterval", TimeSpan.FromHours(6));
    }

    protected override TimeSpan InitialDelay => TimeSpan.FromMinutes(10);

    protected override TimeSpan Interval => interval;

    protected override async Task<JobResult> RunOnceAsync(CancellationToken ct) =>
        JobResult.Success(new Dictionary<string, long> { ["deleted"] = await PurgeAsync(ct) });

    /// <summary>How many notifications were deleted.</summary>
    public async Task<long> PurgeAsync(CancellationToken ct = default)
    {
        var before = clock.GetUtcNow() - retention;
        long total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var deleted = await transactions.RunAsync(DbScopeContext.None,
                tx => tx.QuerySingleAsync<int?>("select purge_notifications(@before, @limit)", new { before, limit = Batch }, ct), ct: ct);
            total += deleted ?? 0;
            if (deleted is null || deleted < Batch)
            {
                break;
            }
        }
        if (total > 0)
        {
            logger.LogInformation("Purged {Count} notifications older than {Before}", total, before);
        }
        return total;
    }
}
