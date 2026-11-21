using System.Collections.Concurrent;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Jobs;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Platform.RateLimit;

/// <summary>
/// Counts attempts per key in fixed windows (Java <c>RateLimiter</c>, ADR 0012). The in-memory implementation is
/// correct for one instance and resets on every deploy. Where a limit must survive a deploy and hold across
/// instances, <see cref="PostgresRateLimiter"/> counts in the database instead (ADR 0012, amended for U1).
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Counts one attempt for <paramref name="key"/>. Throws <see cref="RateLimitedException"/> when more than
    /// <paramref name="limit"/> attempts fall in the current window.
    /// </summary>
    void Acquire(string key, int limit, TimeSpan window);
}

/// <summary>
/// Fixed-window counters in memory (Java <c>InMemoryRateLimiter</c>). Keys are hashed by the caller when they contain
/// personal data. Expired windows are swept at most once a minute, on the next attempt (Java sweeps on a one-minute
/// <c>@Scheduled</c>), so the map cannot grow without bound and no unrecorded background service is needed.
/// </summary>
public sealed class InMemoryRateLimiter(TimeProvider clock) : IRateLimiter
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private sealed class Window(long startMillis, long lengthMillis)
    {
        public long StartMillis { get; } = startMillis;
        public long LengthMillis { get; } = lengthMillis;
        public int Count;
    }

    private readonly ConcurrentDictionary<string, Window> windows = new();
    private long lastSweepMillis = clock.GetUtcNow().ToUnixTimeMilliseconds();

    public void Acquire(string key, int limit, TimeSpan window)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var length = window.Ticks / TimeSpan.TicksPerMillisecond;
        SweepIfDue(now);
        var current = windows.AddOrUpdate(key, _ => new Window(now, length),
            (_, existing) => now - existing.StartMillis >= existing.LengthMillis ? new Window(now, length) : existing);
        if (Interlocked.Increment(ref current.Count) > limit)
        {
            var remaining = current.StartMillis + current.LengthMillis - now;
            throw new RateLimitedException(Math.Max(1, (remaining + 999) / 1000));
        }
    }

    /// <summary>Forgets every window that has ended.</summary>
    public void Sweep()
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var (key, window) in windows)
        {
            if (now - window.StartMillis >= window.LengthMillis)
            {
                windows.TryRemove(new KeyValuePair<string, Window>(key, window));
            }
        }
        Interlocked.Exchange(ref lastSweepMillis, now);
    }

    /// <summary>Tests only: forget every counter.</summary>
    public void Reset() => windows.Clear();

    private void SweepIfDue(long now)
    {
        var last = Interlocked.Read(ref lastSweepMillis);
        if (now - last >= SweepEvery.Ticks / TimeSpan.TicksPerMillisecond && Interlocked.CompareExchange(ref lastSweepMillis, now, last) == last)
        {
            Sweep();
        }
    }
}

/// <summary>
/// Fixed-window counters in PostgreSQL (V22, ADR 0012 amendment; Java <c>PostgresRateLimiter</c>): a limit that must
/// survive a deploy and hold across instances, which <see cref="InMemoryRateLimiter"/> cannot. Used where an anonymous
/// caller could otherwise wait for a deploy, or spread over instances, to get a fresh budget: the public demo form.
/// <para>Windows are aligned to the epoch by the database clock. Each attempt is one upsert in <c>rate_limit_hit</c>,
/// a SECURITY DEFINER function: the runtime role has no grant on the table and cannot read, reset or delete a counter.
/// The subject (an address, a phone, an e-mail) is stored only as a keyed hash, per bucket.</para>
/// <para>Every attempt counts, refused ones included, in its own short transaction (Java <c>REQUIRES_NEW</c>): an
/// attempt that later fails validation, or whose request rolls back, still spends the budget.</para>
/// </summary>
public sealed class PostgresRateLimiter(ITransactions transactions, LookupHasher hasher)
{
    /// <summary>
    /// Counts one attempt by <paramref name="subject"/> in <paramref name="bucket"/>. Throws
    /// <see cref="RateLimitedException"/>, with the seconds until the window ends, when more than
    /// <paramref name="limit"/> attempts fall in the window.
    /// </summary>
    /// <param name="bucket">what is limited and over which window, e.g. <c>demo-ip-hour</c>; a constant, never input</param>
    public async Task AcquireAsync(string bucket, string subject, int limit, TimeSpan window, CancellationToken ct = default)
    {
        var keyHash = hasher.Hash("rate-limit:" + bucket, subject);
        var seconds = checked((int)(window.Ticks / TimeSpan.TicksPerSecond));
        // Its own transaction even when the caller has one: the count must commit either way.
        var hit = await transactions.RunAsync(DbScopeContext.None, tx => tx.QuerySingleAsync<Hit>(
            "select hit_count, retry_after_seconds from rate_limit_hit(@bucket, @key, @seconds)",
            new { bucket, key = keyHash, seconds }, ct), ct: ct);
        if (hit.hit_count > limit)
        {
            throw new RateLimitedException(hit.retry_after_seconds);
        }
    }

    private sealed record Hit(int hit_count, int retry_after_seconds);
}

/// <summary>
/// Keeps <c>rate_limit_counters</c> bounded (V22, ADR 0012 amendment; Java <c>RateLimitCounterRetention</c>): windows
/// that have ended are deleted every hour, in bounded batches, each its own transaction, through
/// <c>purge_rate_limit_counters</c>, which removes nothing that is still running. Two instances only share the work.
/// </summary>
public sealed class RateLimitCounterRetention : ScheduledJob
{
    public const int Batch = 5_000;

    public const int MaxBatchesPerRun = 40;

    private readonly ITransactions transactions;
    private readonly ILogger<RateLimitCounterRetention> logger;

    public RateLimitCounterRetention(Jobs.Jobs jobs, ITransactions transactions, IConfiguration configuration,
        ILogger<RateLimitCounterRetention> logger)
        // Hourly: three missed runs before the health check says DOWN (ADR 0028).
        : base(jobs, "rate-limit-retention", TimeSpan.FromHours(3))
    {
        this.transactions = transactions;
        this.logger = logger;
        Interval = Durations.Get(configuration, "Zawed:RateLimit:PurgeInterval", TimeSpan.FromHours(1));
    }

    protected override TimeSpan InitialDelay => TimeSpan.FromMinutes(5);

    protected override TimeSpan Interval { get; }

    protected override async Task<JobResult> RunOnceAsync(CancellationToken ct) =>
        JobResult.Success(new Dictionary<string, long> { ["deleted"] = await PurgeAsync(ct) });

    /// <returns>how many ended windows were deleted</returns>
    public async Task<long> PurgeAsync(CancellationToken ct = default)
    {
        long total = 0;
        var finished = false;
        for (var batch = 0; batch < MaxBatchesPerRun && !finished; batch++)
        {
            var deleted = await transactions.RunAsync(DbScopeContext.None, tx => tx.QuerySingleAsync<int>(
                "select purge_rate_limit_counters(@limit)", new { limit = Batch }, ct), ct: ct);
            total += deleted;
            finished = deleted < Batch;
        }
        if (total > 0)
        {
            logger.LogInformation("Purged {Count} expired rate-limit windows", total);
        }
        return total;
    }
}
