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
/// <para>The online half. Java's test also sends an offline batch (a recorded capture crossing the threshold and a rejected
/// one); that half comes with offline sync (wave 4A), and the count it adds (3 of Java's 5) with it.</para>
/// </summary>
public sealed class NotificationFailureTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly FuelingTestSupport f = new(api.Factory);

    private NotificationMetrics Metrics => api.Factory.Services.GetRequiredService<NotificationMetrics>();

    [Fact]
    public async Task AFuelingIsRecordedWhenItsNotificationsCannotBeWritten()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var online = await f.CompanyAsync(50_000);
        await f.CreditAsync(online, 60_000);
        var onlineCar = await f.CarAsync(online);
        var before = Dropped();

        await using (var refusal = await RefusalAsync([online], pump.StationId))
        {
            // Online: 60,000 → 45,000 crosses the threshold, which raises a notice and a hard alert.
            var fueled = await f.FuelAsync(pump, onlineCar, 10_000);
            Assert.True((int)fueled.StatusCode == 201, await fueled.Content.ReadAsStringAsync());
            Assert.Equal("COMPLETED", await StatusAsync(fueled));
            Assert.Equal(45_000, await f.WalletBalanceAsync(online));
            Assert.Equal(1, await AlertsAsync(online));

            Assert.Equal(0, await refusal.RefusedAsync()); // nothing about these was written
        }

        // Two notices for the crossing: the company's low balance and Operations' hard alert.
        Assert.Equal(2, Dropped() - before);
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
