using System.Collections.Concurrent;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Platform.RateLimit;

/// <summary>
/// Counts attempts per key in fixed windows (Java <c>RateLimiter</c>). The in-memory implementation is correct
/// for one instance and resets on every deploy; a limit that must survive a deploy and hold across instances
/// counts in PostgreSQL instead (<c>PostgresRateLimiter</c>, ADR 0012 amended for U1).
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Counts one attempt for <paramref name="key"/>. Throws <see cref="RateLimitedException"/>, with the seconds
    /// until the window ends, when more than <paramref name="limit"/> attempts fall in the current window.
    /// </summary>
    void Acquire(string key, int limit, TimeSpan window);
}

/// <summary>
/// Fixed-window counters in memory (Java <c>InMemoryRateLimiter</c>). Keys are hashed by the caller when they
/// contain personal data. Ended windows are swept at most once a minute, on the next attempt after the minute
/// (Java swept on a one-minute schedule), so the map cannot grow without bound and no timer is needed.
/// </summary>
public sealed class InMemoryRateLimiter(TimeProvider clock) : IRateLimiter
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private sealed class Window(long startMillis, long lengthMillis)
    {
        private int count;

        public long StartMillis { get; } = startMillis;
        public long LengthMillis { get; } = lengthMillis;

        public int Increment() => Interlocked.Increment(ref count);
    }

    private readonly ConcurrentDictionary<string, Window> windows = new();
    private long lastSweepMillis = clock.GetUtcNow().ToUnixTimeMilliseconds();

    public void Acquire(string key, int limit, TimeSpan window)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        SweepIfDue(now);
        var length = (long)window.TotalMilliseconds;
        var current = windows.AddOrUpdate(key, _ => new Window(now, length),
            (_, existing) => now - existing.StartMillis >= existing.LengthMillis ? new Window(now, length) : existing);
        if (current.Increment() > limit)
        {
            var remaining = current.StartMillis + current.LengthMillis - now;
            throw new RateLimitedException(Math.Max(1, (remaining + 999) / 1000));
        }
    }

    /// <summary>Removes every window that has ended.</summary>
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
    }

    /// <summary>Tests only: forget every counter.</summary>
    public void Reset() => windows.Clear();

    private void SweepIfDue(long now)
    {
        var last = Interlocked.Read(ref lastSweepMillis);
        if (now - last >= SweepEvery.TotalMilliseconds && Interlocked.CompareExchange(ref lastSweepMillis, now, last) == last)
        {
            Sweep();
        }
    }
}
