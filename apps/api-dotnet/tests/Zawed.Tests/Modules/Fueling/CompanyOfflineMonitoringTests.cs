using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;
using static Zawed.Tests.Modules.Fueling.OfflineTestSupport;

namespace Zawed.Tests.Modules.Fueling;

/// <summary>
/// The application with Java's <c>@TestPropertySource</c> of <c>CompanyOfflineMonitoringTests</c>: a device may record 10,000
/// piastres offline per company in 24 hours, and a company's offline fuelings alert at 40,000.
/// </summary>
public sealed class OfflineMonitoringApi : IAsyncLifetime
{
    public ZawedApiFactory Factory { get; } = new(settings: new Dictionary<string, string?>
    {
        ["Zawed:Sync:OfflineExposureCapPiastres"] = "10000",
        ["Zawed:Sync:CompanyOfflineAlertPiastres"] = "40000",
    });

    public Task InitializeAsync()
    {
        _ = Factory.Services; // start the host
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>
/// Company-wide offline monitoring (V14, ADR 0025; Java <c>CompanyOfflineMonitoringTests</c>): a disconnected terminal cannot
/// know what other terminals spent, so the server raises one alert when a company's offline fuelings across every device in
/// 24 hours cross the threshold. It never blocks or changes a fueling. With these settings every 30,000 capture is also past
/// one device's exposure cap, so the per-device bound is checked here too.
/// </summary>
public sealed class CompanyOfflineMonitoringTests(OfflineMonitoringApi api) : IClassFixture<OfflineMonitoringApi>
{
    private readonly OfflineTestSupport o = new(api.Factory);

    private FuelingTestSupport F => o.F;

    [Fact]
    public async Task CrossingTheCompanyThresholdAcrossDevicesRaisesOneAlert()
    {
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 1_000_000);
        var first = await F.PumpAsync();
        var second = await F.PumpAsync();
        await F.PriceAsync(first.StationId, "GAS_92", Price);
        await F.PriceAsync(second.StationId, "GAS_92", Price);

        await o.SyncAsync(first, await o.CaptureAsync(first, await F.CarAsync(company), 20_000)); // 30,000: under the 40,000 threshold
        Assert.Equal(0, await AlertsAsync(company));
        await o.SyncAsync(second, await o.CaptureAsync(second, await F.CarAsync(company), 20_000)); // 60,000 across two devices: crosses it
        Assert.Equal(1, await AlertsAsync(company));
        await o.SyncAsync(first, await o.CaptureAsync(first, await F.CarAsync(company), 10_000)); // still above: no second alert
        Assert.Equal(1, await AlertsAsync(company));

        // Monitoring only: every capture was recorded and completed.
        Assert.Equal(3, await o.CountAsync("select count(*) from fuelings where company_id = @c::uuid and origin = 'OFFLINE' and status = 'COMPLETED'",
            new { c = company }));
    }

    [Fact]
    public async Task CapturesFromManyDevicesSyncedAtOnceRaiseTheAlertExactlyOnce()
    {
        // Every fueling of a company takes the company's wallet row lock first (ADR 0018), and the 24-hour sum and the
        // crossing decision are read after it, under READ COMMITTED, so each capture sees every capture committed before it.
        // Any two of these captures cross the threshold, so without that serialisation two parallel ones would both see only
        // themselves (missed) or both see each other (duplicated).
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 1_000_000);
        const int devices = 6;
        var pumps = new List<Network.Pump>();
        var captures = new List<Capture>();
        for (var i = 0; i < devices; i++)
        {
            var pump = await F.PumpAsync();
            await F.PriceAsync(pump.StationId, "GAS_92", Price);
            pumps.Add(pump);
            captures.Add(await o.CaptureAsync(pump, await F.CarAsync(company), 20_000)); // 30,000 each
        }

        var statuses = await Concurrently.RunAsync(devices, async i => (int)(await o.SyncAsync(pumps[i], captures[i])).StatusCode);
        Assert.All(statuses, s => Assert.Equal(200, s));
        Assert.Equal(devices, await o.CountAsync("select count(*) from fuelings where company_id = @c::uuid and origin = 'OFFLINE'", new { c = company }));

        Assert.Equal(1, await AlertsAsync(company));
        // Raised by the second capture to commit, which saw exactly one before it: 2 × 30,000.
        Assert.Equal(60_000, await o.Owner.QueryOneAsync<long>("""
            select (details ->> 'offline24hPiastres')::bigint from alerts
            where type = 'COMPANY_OFFLINE_EXPOSURE' and company_id = @c::uuid
            """, new { c = company }));
    }

    [Fact]
    public async Task ALateCaptureFromOutsideTheWindowNeverRaisesItAgain()
    {
        // A capture dated two days ago is outside the 24-hour window it is summed in, so it cannot be the one that crosses the
        // threshold (V17's fix).
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 1_000_000);
        var first = await F.PumpAsync();
        var second = await F.PumpAsync();
        var late = await F.PumpAsync();
        foreach (var pump in new[] { first, second, late })
        {
            await F.PriceAsync(pump.StationId, "GAS_92", Price);
        }
        await o.SyncAsync(first, await o.CaptureAsync(first, await F.CarAsync(company), 20_000));
        await o.SyncAsync(second, await o.CaptureAsync(second, await F.CarAsync(company), 20_000)); // 60,000: crosses
        Assert.Equal(1, await AlertsAsync(company));

        var old = await o.SyncAsync(late, (await o.CaptureAsync(late, await F.CarAsync(company), 20_000)).At(DateTimeOffset.UtcNow.AddDays(-2)));
        Assert.Equal(["RECORDED"], await ResultsAsync(old));
        Assert.Equal(1, await AlertsAsync(company)); // still one alert
    }

    [Fact]
    public async Task PastTheDeviceBoundTheFuelingIsRecordedFlaggedItsPayableHeldAndOperationsAlerted()
    {
        // B5 review 2: one device's offline spending for one company in 24 hours past the cap (10,000 here). The fueling is
        // recorded and charged all the same; its payable is held for Operations; a critical OFFLINE_EXPOSURE alert names the
        // device; a fueling under the cap is not flagged.
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 1_000_000);
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);

        var under = await AnsweredAsync(await o.SyncAsync(pump, await o.CaptureAsync(pump, await F.CarAsync(company), 6_000))); // 9,000
        Assert.Empty(Conflicts(under, 0));
        Assert.Equal(false, (await o.FuelingRowAsync(FuelingId(under, 0)))["payable_held"]);

        var over = await AnsweredAsync(await o.SyncAsync(pump, await o.CaptureAsync(pump, await F.CarAsync(company), 1_000))); // 10,500
        Assert.Equal(["OVER_OFFLINE_ALLOWANCE"], Conflicts(over, 0));
        var row = await o.FuelingRowAsync(FuelingId(over, 0));
        Assert.Equal(true, row["payable_held"]);
        Assert.Equal("COMPLETED", row["status"]);
        Assert.Equal(1_000_000 - 10_500, await o.WalletBalanceAsync(company));
        Assert.Equal(10_500, await o.StationPayableAsync(pump.StationId));
        var alert = await o.RowAsync("select severity, details::text as details, company_visible from alerts where type = 'OFFLINE_EXPOSURE' and fueling_id = @f::uuid",
            new { f = FuelingId(over, 0) });
        Assert.Equal("CRITICAL", alert["severity"]);
        Assert.Equal(false, alert["company_visible"]);
        Assert.Contains("\"capPiastres\": 10000", (string)alert["details"]!, StringComparison.Ordinal);
        Assert.Contains("\"amountPiastres\": 1500", (string)alert["details"]!, StringComparison.Ordinal);
        Assert.Contains("\"deviceCode\": \"" + pump.DeviceCode + "\"", (string)alert["details"]!, StringComparison.Ordinal);
        Assert.Equal("CRITICAL", await o.Owner.QueryOneAsync<string>(
            "select severity from alerts where type = 'SYNC_CONFLICT' and fueling_id = @f::uuid", new { f = FuelingId(over, 0) }));

        // Another device's spending is its own: the same company at a second pump starts from zero.
        var other = await F.PumpAsync();
        await F.PriceAsync(other.StationId, "GAS_92", Price);
        Assert.Empty(Conflicts(await AnsweredAsync(await o.SyncAsync(other, await o.CaptureAsync(other, await F.CarAsync(company), 1_000))), 0));
    }

    private async Task<long> AlertsAsync(string company) =>
        await o.CountAsync("select count(*) from alerts where type = 'COMPANY_OFFLINE_EXPOSURE' and company_id = @c::uuid", new { c = company });
}
