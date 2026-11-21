using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Notifications;
using Zawed.Tests.Modules.Fueling;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;

namespace Zawed.Tests.Modules.Notifications;

/// <summary>
/// A fueling is recorded when its notifications cannot be written (B7 W5 review, M1; Java <c>fueling.NotificationFailureTests</c>):
/// the database refuses every notice about the company, and the fueling still commits with its posting, balance and
/// low-balance alert, while the refused notices are rolled back to their savepoints and counted.
/// <para>Online and offline, as Java's test: the same crossing inside a sync item is still recorded, a capture that cannot
/// be recorded at all is still answered as rejected (its SYNC_REJECTED alert's notice refused too), and the batch is still
/// answered and logged.</para>
/// </summary>
public sealed class NotificationFailureTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly OfflineTestSupport offline = new(api.Factory);

    private FuelingTestSupport f => offline.F;

    private NotificationMetrics Metrics => api.Factory.Services.GetRequiredService<NotificationMetrics>();

    [Fact]
    public async Task AFuelingIsRecordedWhenItsNotificationsCannotBeWritten()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var online = await f.CompanyAsync(50_000);
        var offlineCompany = await f.CompanyAsync(50_000);
        await f.CreditAsync(online, 60_000);
        await f.CreditAsync(offlineCompany, 60_000);
        var onlineCar = await f.CarAsync(online);
        var offlineCar = await f.CarAsync(offlineCompany);
        var before = Dropped();

        await using (var refusal = await RefusalAsync([online, offlineCompany], pump.StationId))
        {
            // Online: 60,000 → 45,000 crosses the threshold, which raises a notice and a hard alert.
            var fueled = await f.FuelAsync(pump, onlineCar, 10_000);
            Assert.True((int)fueled.StatusCode == 201, await fueled.Content.ReadAsStringAsync());
            Assert.Equal("COMPLETED", await StatusAsync(fueled));
            Assert.Equal(45_000, await f.WalletBalanceAsync(online));
            Assert.Equal(1, await AlertsAsync(online));

            // Offline: the same crossing inside a sync item, and a capture that cannot be recorded at all.
            var recorded = await offline.CaptureAsync(pump, offlineCar, 10_000);
            var unknownTag = new Capture(Guid.NewGuid(), RandomUid(), 10_000, null, DateTimeOffset.UtcNow, await offline.AgentIdAsync(pump));
            var batch = Guid.NewGuid();
            var answer = await offline.SyncAsync(pump, batch, 0, [recorded, unknownTag]);
            Assert.Equal(["RECORDED", "REJECTED"], await OfflineTestSupport.ResultsAsync(answer));
            Assert.Equal(45_000, await f.WalletBalanceAsync(offlineCompany));
            Assert.Equal(1, await AlertsAsync(offlineCompany));
            Assert.Equal(1, await f.CountAsync("select count(*) from alerts where type = 'SYNC_REJECTED' and device_id = @d::uuid",
                new { d = pump.DeviceId }));
            // The batch was answered and logged, not turned into a 500.
            Assert.Equal(1, await f.CountAsync("select count(*) from sync_batches where device_batch_id = @batch", new { batch }));

            Assert.Equal(0, await refusal.RefusedAsync()); // nothing about these was written
        }

        // Two notices per crossing (online and offline) and one for the rejected capture's alert.
        Assert.Equal(5, Dropped() - before);
    }

    [Fact]
    public async Task WithoutARefusalTheSameFuelingWritesItsNotifications()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync(50_000);
        await f.CreditAsync(company, 60_000);
        Assert.Equal(201, (int)(await f.FuelAsync(pump, await f.CarAsync(company), 10_000)).StatusCode);
        Assert.Equal(2, await f.CountAsync("""
            select count(*) from notifications
            where (type = 'WALLET_LOW_BALANCE' and entity_id = @company::uuid)
               or (type = 'HARD_ALERT_RAISED' and params ->> 'companyId' = @company)
            """, new { company }));
    }

    // ------------------------------------------------------------------------------------------------------- helpers

    private long Dropped() => Metrics.Dropped(NotificationType.WALLET_LOW_BALANCE) + Metrics.Dropped(NotificationType.HARD_ALERT_RAISED);

    private Task<long> AlertsAsync(string company) =>
        f.CountAsync("select count(*) from alerts where company_id = @company::uuid and type = 'LOW_BALANCE'", new { company });

    /// <summary>A trigger that refuses every notification about these companies or this station, until disposed.</summary>
    private async Task<Refusal> RefusalAsync(IReadOnlyList<string> companies, string station)
    {
        var name = "test_refuse_" + Guid.NewGuid().ToString("N");
        var ids = string.Join(",", companies.Select(c => $"'{Guid.Parse(c)}'"));
        await f.Owner.ExecuteAsync($"create function {name}() returns trigger language plpgsql as $$ begin "
            + "raise exception 'notification refused by the test'; end $$");
        await f.Owner.ExecuteAsync($"create trigger {name} before insert on notifications for each row when ("
            + $"new.entity_id in ({ids}) or new.params ->> 'companyId' in ({ids}) "
            + $"or new.params ->> 'stationId' = '{Guid.Parse(station)}') execute function {name}()");
        return new Refusal(f.Owner, name, companies, station);
    }

    private sealed record Refusal(Owner Owner, string Name, IReadOnlyList<string> Companies, string Station) : IAsyncDisposable
    {
        private const string FuelingNotices = "type in ('WALLET_LOW_BALANCE', 'HARD_ALERT_RAISED') and ";

        /// <summary>What a fueling would have caused about them (the earlier top-up notices are not counted).</summary>
        public async Task<long> RefusedAsync()
        {
            long count = 0;
            foreach (var company in Companies)
            {
                count += await Owner.QueryOneAsync<long>("select count(*) from notifications where " + FuelingNotices
                    + "(entity_id = @company::uuid or params ->> 'companyId' = @company)", new { company });
            }
            return count + await Owner.QueryOneAsync<long>("select count(*) from notifications where " + FuelingNotices
                + "params ->> 'stationId' = @station", new { station = Station });
        }

        public async ValueTask DisposeAsync()
        {
            await Owner.ExecuteAsync($"drop trigger {Name} on notifications");
            await Owner.ExecuteAsync($"drop function {Name}()");
        }
    }
}
