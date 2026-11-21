using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Web;
using Zawed.Tests.Support;

namespace Zawed.Tests.Platform;

/// <summary>
/// The shared limiter (V22, ADR 0012 amendment; Java <c>PostgresRateLimiterTests</c>) against the real database. Two
/// "instances" are two limiters on two separate connection pools, as two application instances would be; a restart is
/// a new limiter on a new pool, with nothing carried over in memory.
/// </summary>
public sealed class PostgresRateLimiterTests(SharedApi api) : IClassFixture<SharedApi>
{
    private LookupHasher Hasher => api.Factory.Services.GetRequiredService<LookupHasher>();

    private Owner Owner => api.Factory.Owner;

    private static string Subject() => "subject-" + Guid.NewGuid();

    private PostgresRateLimiter Limiter(Npgsql.NpgsqlDataSource pool) => new(RuntimeConnections.TransactionsOver(pool), Hasher);

    [Fact]
    public async Task TwoInstancesCountingAtTheSameMomentShareOneBudget()
    {
        var subject = Subject();
        await using (var poolA = await RuntimeConnections.PoolAsync())
        await using (var poolB = await RuntimeConnections.PoolAsync())
        {
            var a = Limiter(poolA);
            var b = Limiter(poolB);
            using var start = new Barrier(12);
            var allowed = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
            {
                start.SignalAndWait();
                try
                {
                    await (i % 2 == 0 ? a : b).AcquireAsync("test-shared", subject, 5, TimeSpan.FromHours(1));
                    return true;
                }
                catch (RateLimitedException)
                {
                    return false;
                }
            })));
            Assert.Equal(5, allowed.Count(x => x));
        }
        Assert.Equal(12, await Owner.QueryOneAsync<int>("select hits from rate_limit_counters where bucket = 'test-shared' and key_hash = @k",
            new { k = Hasher.Hash("rate-limit:test-shared", subject) }));
    }

    [Fact]
    public async Task ARestartedInstanceKeepsCountingWhereTheLastOneStopped()
    {
        var subject = Subject();
        await using (var before = await RuntimeConnections.PoolAsync())
        {
            var running = Limiter(before);
            for (var i = 0; i < 3; i++)
            {
                await running.AcquireAsync("test-restart", subject, 3, TimeSpan.FromDays(1));
            }
        }
        await using var after = await RuntimeConnections.PoolAsync();
        var refused = await Assert.ThrowsAsync<RateLimitedException>(() => Limiter(after).AcquireAsync("test-restart", subject, 3, TimeSpan.FromDays(1)));
        Assert.InRange(refused.RetryAfterSeconds, 1, 86_400);
        Assert.Equal((object)refused.RetryAfterSeconds, refused.Params!["retryAfterSeconds"]);
    }

    [Fact]
    public async Task ANewWindowStartsAFreshBudgetAndKeysAndBucketsAreIndependent()
    {
        var subject = Subject();
        await using var pool = await RuntimeConnections.PoolAsync();
        var limiter = Limiter(pool);
        await limiter.AcquireAsync("test-window", subject, 1, TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<RateLimitedException>(() => limiter.AcquireAsync("test-window", subject, 1, TimeSpan.FromHours(1)));
        // Someone else, or the same subject in another bucket, is not affected.
        await limiter.AcquireAsync("test-window", Subject(), 1, TimeSpan.FromHours(1));
        await limiter.AcquireAsync("test-other", subject, 1, TimeSpan.FromHours(1));

        await Owner.ExecuteAsync("""
            update rate_limit_counters set window_start = window_start - interval '1 hour', expires_at = expires_at - interval '1 hour'
            where bucket = 'test-window' and key_hash = @k
            """, new { k = Hasher.Hash("rate-limit:test-window", subject) });
        await limiter.AcquireAsync("test-window", subject, 1, TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task OnlyAKeyedHashOfTheSubjectIsStored()
    {
        const string subject = "203.0.113.77";
        await using (var pool = await RuntimeConnections.PoolAsync())
        {
            await Limiter(pool).AcquireAsync("test-hash", subject, 10, TimeSpan.FromMinutes(1));
        }
        var keys = await Owner.QueryAsync<string>("select key_hash from rate_limit_counters where bucket = 'test-hash'");
        Assert.NotEmpty(keys);
        Assert.All(keys, k =>
        {
            Assert.Matches("^[0-9a-f]{64}$", k);
            Assert.DoesNotContain("203", k);
        });
        Assert.DoesNotContain(SecureTokens.Sha256Hex(subject), keys);
    }

    [Fact]
    public async Task EndedWindowsArePurgedAndRunningOnesKept()
    {
        var ended = Hasher.Hash("rate-limit:test-purge", Subject());
        var running = Hasher.Hash("rate-limit:test-purge", Subject());
        await Owner.ExecuteAsync("""
            insert into rate_limit_counters (bucket, key_hash, window_start, expires_at, hits)
            values ('test-purge', @ended, now() - interval '2 days', now() - interval '1 day', 7),
                   ('test-purge', @running, now() - interval '1 minute', now() + interval '1 hour', 7)
            """, new { ended, running });

        Assert.True(await api.Factory.Services.GetRequiredService<RateLimitCounterRetention>().PurgeAsync() >= 1);
        Assert.Equal([running], await Owner.QueryAsync<string>("select key_hash from rate_limit_counters where bucket = 'test-purge'"));
    }

    [Fact]
    public async Task TheRuntimeRoleCanCountButNeverReadResetOrDeleteACounter()
    {
        string[] statements =
        [
            "select * from rate_limit_counters", "delete from rate_limit_counters", "update rate_limit_counters set hits = 0",
            "insert into rate_limit_counters values ('x-y', '" + new string('0', 64) + "', now(), now() + interval '1 hour', 1)",
        ];
        foreach (var sql in statements)
        {
            var refusal = await RuntimeConnections.RefusalAsync(Owner, null, sql);
            Assert.True(refusal?.MessageText.Contains("permission denied") == true, sql);
        }
        var window = await RuntimeConnections.RefusalAsync(Owner, null,
            "select * from rate_limit_hit('test-guard', '" + new string('0', 64) + "', 0)");
        Assert.Contains("a window is 1 second to 7 days", window!.MessageText);
    }

    // ----------------------------------------------------------------------- the in-memory limiter

    [Fact]
    public void TheInMemoryLimiterCountsAFixedWindowPerKeyAndSaysWhenToComeBack()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-21T10:00:00Z"));
        var limiter = new InMemoryRateLimiter(clock);
        for (var i = 0; i < 3; i++)
        {
            limiter.Acquire("k", 3, TimeSpan.FromMinutes(1));
        }
        clock.Advance(TimeSpan.FromSeconds(20.5));
        var refused = Assert.Throws<RateLimitedException>(() => limiter.Acquire("k", 3, TimeSpan.FromMinutes(1)));
        Assert.Equal(40, refused.RetryAfterSeconds); // 39.5 s rounded up
        Assert.Equal(429, refused.Status);
        Assert.Equal("RATE_LIMITED", refused.Code);
        limiter.Acquire("other", 3, TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromSeconds(40));
        limiter.Acquire("k", 3, TimeSpan.FromMinutes(1)); // a new window
        limiter.Reset();
        for (var i = 0; i < 3; i++)
        {
            limiter.Acquire("k", 3, TimeSpan.FromMinutes(1));
        }
    }
}
