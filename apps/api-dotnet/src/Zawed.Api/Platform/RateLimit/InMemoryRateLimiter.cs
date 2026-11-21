using System.Collections.Concurrent;
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
