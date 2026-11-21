using Zawed.Api.Platform.Jobs;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Modules.Identity;

/// <summary>
/// Keeps the web sign-in tables bounded (V21, ADR 0028; Java <c>SessionRetention</c>).
/// <list type="bullet">
/// <item><b>Sessions</b> are deleted <c>Zawed:Sessions:Retention</c> (90 days by default, never under
/// <see cref="SessionMinimum"/>) after they <em>ended</em>: revoked, idle-expired or past their absolute end, whichever
/// came first. The audit log keeps every sign-in, sign-out and revocation for ever. A live session ends in the
/// future, so no cutoff can reach it.</item>
/// <item><b>MFA challenges</b> are deleted <c>Zawed:Sessions:MfaChallengeRetention</c> (one day by default, never under
/// <see cref="ChallengeMinimum"/>) after they expired.</item>
/// </list>
/// Both go through bounded SECURITY DEFINER functions (<c>purge_sessions</c>, <c>purge_mfa_challenges</c>) that refuse a
/// cutoff inside their own floor: the runtime role has no DELETE on either table. Each batch is its own short
/// transaction, and rows are taken with <c>SKIP LOCKED</c>, so a purge never waits behind a sign-in or a refresh. Two
/// instances running it at once only share the work. Station agents' sessions (<c>agent_sessions</c>) are never purged.
/// </summary>
public sealed class SessionRetention : ScheduledJob
{
    /// <summary>What <c>purge_sessions</c> refuses to go under, whoever calls it.</summary>
    public static readonly TimeSpan SessionFloor = TimeSpan.FromDays(30);

    /// <summary>The floor plus a day, so a clock ahead of the database's never trips the function's floor.</summary>
    public static readonly TimeSpan SessionMinimum = SessionFloor + TimeSpan.FromDays(1);

    /// <summary>What <c>purge_mfa_challenges</c> refuses to go under.</summary>
    public static readonly TimeSpan ChallengeFloor = TimeSpan.FromHours(1);

    public static readonly TimeSpan ChallengeMinimum = ChallengeFloor + TimeSpan.FromHours(1);

    public const int Batch = 5_000;

    /// <summary>A run stops after this many batches per table and finishes on the next run.</summary>
    public const int MaxBatchesPerRun = 40;

    private readonly Jobs jobs;
    private readonly ITransactions transactions;
    private readonly TimeProvider clock;
    private readonly ILogger<SessionRetention> logger;
    private readonly TimeSpan sessionRetention;
    private readonly TimeSpan challengeRetention;
    private readonly TimeSpan interval;

    public SessionRetention(Jobs jobs, ITransactions transactions, TimeProvider clock, IConfiguration configuration, ILogger<SessionRetention> logger)
        : base(jobs, "session-retention", TimeSpan.FromHours(4))
    {
        this.jobs = jobs;
        this.transactions = transactions;
        this.clock = clock;
        this.logger = logger;
        sessionRetention = AtLeast(configuration.GetValue("Zawed:Sessions:Retention", TimeSpan.FromDays(90)), SessionMinimum);
        challengeRetention = AtLeast(configuration.GetValue("Zawed:Sessions:MfaChallengeRetention", TimeSpan.FromDays(1)), ChallengeMinimum);
        interval = configuration.GetValue("Zawed:Sessions:PurgeInterval", TimeSpan.FromHours(1));
    }

    protected override TimeSpan InitialDelay => TimeSpan.FromMinutes(7);

    protected override TimeSpan Interval => interval;

    public static TimeSpan AtLeast(TimeSpan configured, TimeSpan minimum) => configured < minimum ? minimum : configured;

    protected override async Task<JobResult> RunOnceAsync(CancellationToken ct)
    {
        var sessions = await PurgeSessionsAsync(ct);
        var challenges = await PurgeChallengesAsync(ct);
        return JobResult.Success(new Dictionary<string, long> { ["sessions"] = sessions, ["mfaChallenges"] = challenges });
    }

    /// <summary>How many ended sessions were deleted.</summary>
    public async Task<long> PurgeSessionsAsync(CancellationToken ct = default)
    {
        var before = clock.GetUtcNow() - sessionRetention;
        var total = await PurgeAsync("purge_sessions", before, ct);
        if (total > 0)
        {
            logger.LogInformation("Purged {Count} sessions that ended before {Before}", total, before);
        }
        return total;
    }

    /// <summary>How many expired MFA challenges were deleted.</summary>
    public async Task<long> PurgeChallengesAsync(CancellationToken ct = default)
    {
        var before = clock.GetUtcNow() - challengeRetention;
        var total = await PurgeAsync("purge_mfa_challenges", before, ct);
        if (total > 0)
        {
            logger.LogInformation("Purged {Count} MFA challenges that expired before {Before}", total, before);
        }
        return total;
    }

    private async Task<long> PurgeAsync(string function, DateTimeOffset before, CancellationToken ct)
    {
        long total = 0;
        var finished = false;
        for (var batch = 0; batch < MaxBatchesPerRun && !finished; batch++)
        {
            // The function name is one of the two constants above, never input.
            var deleted = await transactions.RunAsync(DbScopeContext.None,
                tx => tx.QuerySingleAsync<int>($"select {function}(@before, @limit)", new { before = before.UtcDateTime, limit = Batch }, ct), ct: ct);
            total += deleted;
            finished = deleted < Batch;
        }
        if (!finished)
        {
            logger.LogWarning("{Function} stopped after {Batches} batches; the rest is deleted on the next run", function, MaxBatchesPerRun);
        }
        return total;
    }
}
