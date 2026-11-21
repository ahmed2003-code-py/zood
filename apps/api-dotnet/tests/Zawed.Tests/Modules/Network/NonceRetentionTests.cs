using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zawed.Api.Modules.Network;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Network;

/// <summary>Device nonces are purged once they are past any replay window, and never sooner (V14, audit F5; Java <c>NonceRetentionTests</c>).</summary>
public sealed class NonceRetentionTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly PosTestSupport pos = new(api.Factory);

    [Fact]
    public async Task OldNoncesArePurgedAndRecentOnesKept()
    {
        var device = Guid.Parse((await pos.RegisterDeviceAsync(await pos.StationAsync(await pos.OperatorAsync()))).Id);
        for (var i = 0; i < 12; i++)
        {
            await pos.Owner.ExecuteAsync("insert into device_nonces (device_id, nonce, created_at) values (@device, @nonce, now() - interval '3 hours')",
                new { device, nonce = "old-nonce-000000-" + i });
        }
        await pos.Owner.ExecuteAsync("insert into device_nonces (device_id, nonce, created_at) values (@device, 'fresh-nonce-00000', now() - interval '5 minutes')",
            new { device });

        Assert.True(await api.Factory.Services.GetRequiredService<NonceRetention>().PurgeAsync() >= 12);
        Assert.Equal(1L, await pos.Owner.QueryOneAsync<long>("select count(*) from device_nonces where device_id = @device", new { device }));
        Assert.Equal("fresh-nonce-00000", await pos.Owner.QueryOneAsync<string>("select nonce from device_nonces where device_id = @device", new { device }));
    }

    [Fact]
    public async Task TheFunctionRefusesToPurgeInsideTheReplayWindow()
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() => pos.Owner.QueryOneAsync<int>("select purge_device_nonces(now() - interval '5 minutes', 10)"));
        Assert.Contains("replay window", refused.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunIsRecordedAsAJob()
    {
        var result = await api.Factory.Services.GetRequiredService<NonceRetention>().RunNowAsync();
        Assert.Equal(Zawed.Api.Platform.Jobs.JobOutcome.SUCCESS, result.Outcome);
        Assert.True(result.Counts.ContainsKey("deleted"));
        Assert.True(await pos.Owner.QueryOneAsync<bool>(
            "select exists (select 1 from job_runs where job_name = 'nonce-retention' and outcome = 'SUCCESS' and started_at > now() - interval '1 minute')"));
    }
}
