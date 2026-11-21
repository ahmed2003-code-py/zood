using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zawed.Api.Platform.Persistence;
using Zawed.Tests.Support;

namespace Zawed.Tests.Platform;

/// <summary>
/// Java <c>IdempotencyKeyRetentionTests</c>: idempotency keys are purged once no client can still be retrying with them
/// (V17, ADR 0022), in bounded batches, through one bounded function that refuses a cutoff inside seven days. The
/// table itself stays append only and per user for every session of the runtime role, a POS route (the platform scope
/// with no user) included.
/// </summary>
public sealed class IdempotencyKeyRetentionTests(SharedApi api) : IClassFixture<SharedApi>
{
    private Owner Owner => api.Factory.Owner;

    private Task Plant(Guid actor, string key, string age) => Owner.ExecuteAsync("""
        insert into idempotency_keys (actor_id, operation, key, request_hash, resource_id, created_at)
        values (@actor, 'wallet_adjustment.create', @key, repeat('a', 64), gen_random_uuid(), now() - cast(@age as interval))
        """, new { actor, key, age });

    [Fact]
    public async Task KeysPastTheRetentionArePurgedAndRecentOnesKept()
    {
        var actor = Guid.NewGuid();
        for (var i = 0; i < 12; i++)
        {
            await Plant(actor, "old-key-" + i, "40 days");
        }
        await Plant(actor, "kept-key-29-days", "29 days"); // inside the 30-day default
        await Plant(actor, "kept-key-fresh", "1 minute");

        Assert.True(await api.Factory.Services.GetRequiredService<IdempotencyKeyRetention>().PurgeAsync() >= 12);
        Assert.Equal(["kept-key-29-days", "kept-key-fresh"],
            await Owner.QueryAsync<string>("select key from idempotency_keys where actor_id = @actor order by key", new { actor }));
    }

    [Fact]
    public void TheJobNeverAsksForLessThanTheDatabaseFloorPlusADay()
    {
        Assert.Equal(TimeSpan.FromDays(7), IdempotencyKeyRetention.DatabaseFloor);
        Assert.Equal(TimeSpan.FromDays(8), IdempotencyKeyRetention.Effective(TimeSpan.FromDays(1)));
        Assert.Equal(TimeSpan.FromDays(8), IdempotencyKeyRetention.Effective(TimeSpan.FromDays(7)));
        Assert.Equal(TimeSpan.FromDays(30), IdempotencyKeyRetention.Effective(TimeSpan.FromDays(30)));
    }

    [Fact]
    public async Task TheFunctionRefusesACutoffInsideSevenDaysWhoeverCallsIt()
    {
        var actor = Guid.NewGuid();
        await Plant(actor, "young-key-2d", "2 days");
        await Plant(actor, "old-key-40d", "40 days");
        foreach (var cutoff in new[] { "now() - interval '2 days'", "now() - interval '6 days 23 hours'", "null" })
        {
            var refusal = await RuntimeConnections.RefusalAsync(Owner, "PLATFORM", $"select purge_idempotency_keys({cutoff}, 100)");
            Assert.True(refusal?.MessageText.Contains("7-day minimum retention") == true, cutoff);
        }
        // Eight days is allowed: the old key goes, the two-day-old one stays.
        var (connection, tx) = await RuntimeConnections.OpenAsync(Owner, "PLATFORM");
        await using (connection)
        await using (tx)
        {
            await using var purge = new NpgsqlCommand("select purge_idempotency_keys(now() - interval '8 days', 50000)", connection, tx);
            Assert.True((int)(await purge.ExecuteScalarAsync())! >= 1);
            await tx.CommitAsync();
        }
        Assert.Equal(["young-key-2d"], await Owner.QueryAsync<string>("select key from idempotency_keys where actor_id = @actor", new { actor }));
    }

    [Fact]
    public async Task APosRouteSessionCanNeitherReadNorDeleteAnyonesKeys()
    {
        // Every POS route runs in the platform scope with no user. Such a session must see no key and delete none.
        var actor = Guid.NewGuid();
        await Plant(actor, "old-key-40d-pos", "40 days");
        await Plant(actor, "mid-key-10d-pos", "10 days");
        var (connection, tx) = await RuntimeConnections.OpenAsync(Owner, "PLATFORM");
        await using (connection)
        await using (tx)
        {
            await using var count = new NpgsqlCommand("select count(*) from idempotency_keys", connection, tx);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
        foreach (var scope in new[] { "PLATFORM", "COMPANY", null })
        {
            foreach (var user in new Guid?[] { null, actor, Guid.NewGuid() })
            {
                var refusal = await RuntimeConnections.RefusalAsync(Owner, scope, $"delete from idempotency_keys where actor_id = '{actor}'", user);
                Assert.True(refusal?.MessageText.Contains("permission denied") == true, $"delete as {scope} / {user}");
            }
        }
        Assert.Equal(2L, await Owner.QueryOneAsync<long>("select count(*) from idempotency_keys where actor_id = @actor", new { actor }));
    }

    [Fact]
    public async Task KeysAreStillNeverUpdated()
    {
        var actor = Guid.NewGuid();
        await Plant(actor, "old-key-update", "40 days");
        var refusal = await RuntimeConnections.RefusalAsync(Owner, "PLATFORM",
            $"update idempotency_keys set resource_id = gen_random_uuid() where actor_id = '{actor}'", actor);
        Assert.Contains("permission denied", refusal!.MessageText);
    }
}
