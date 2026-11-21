using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;

namespace Zawed.Tests.Modules.Fueling;

/// <summary>The application with the VIP flow switched on (Java's <c>@TestPropertySource(zawed.fueling.vip-enabled=true)</c>).</summary>
public sealed class VipApi : IAsyncLifetime
{
    public ZawedApiFactory Factory { get; } = new(settings: new Dictionary<string, string?> { ["Zawed:Fueling:VipEnabled"] = "true" });

    public Task InitializeAsync()
    {
        _ = Factory.Services; // start the host
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>
/// The VIP flow as modelled (PRD 4.4, 4.5; Java <c>VipFuelingTests</c>), with its switch on. It ships switched off until the
/// owner decides who pays for VIP fuel; <see cref="FuelingTests"/> covers the switched-off behaviour.
/// </summary>
public sealed class VipFuelingTests(VipApi api) : IClassFixture<VipApi>
{
    private readonly FuelingTestSupport f = new(api.Factory);

    [Fact]
    public async Task AVipFuelingIsRecordedButNotChargedAndItsCeilingFlagsOnlyToOps()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        var vip = await f.CarAsync(company, tier: "VIP", vipCeilingMl: 30_000);

        var first = await BodyAsync(await f.FuelAsync(pump, vip, 25_000), 201);
        Assert.Equal("COMPLETED", first.GetProperty("status").GetString()); // VIP skips the wallet: the company has no money
        Assert.False(first.GetProperty("billable").GetBoolean());
        Assert.Equal(0, await f.WalletBalanceAsync(company));
        Assert.Null(await f.Owner.QueryOneAsync<Guid?>("select ledger_transaction_id from fuelings where id = @id::uuid",
            new { id = first.GetProperty("id").GetString() }));

        await f.Owner.ExecuteAsync("update rfid_tags set cooldown_until = null where vehicle_id = @v::uuid", new { v = vip.VehicleId });
        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, vip, 10_000))); // 35 L > 30 L ceiling

        var types = (await BodyAsync(await f.GetAsync($"/api/v1/ops/alerts?companyId={company}", await f.OpsAsync()), 200))
            .GetProperty("items").EnumerateArray().Select(a => a.GetProperty("type").GetString());
        Assert.Equal(new[] { "VIP_CEILING_PASSED" }, types);
        var manager = await f.SignInAsync(Role.FLEET_MANAGER, Scope.Company(Guid.Parse(company)));
        var companyAlerts = await BodyAsync(await f.GetAsync("/api/v1/company/alerts", manager), 200);
        Assert.Equal(0, companyAlerts.GetProperty("items").GetArrayLength()); // the ceiling is invisible to the company

        // Past the ceiling, it is not raised again.
        await f.Owner.ExecuteAsync("update rfid_tags set cooldown_until = null where vehicle_id = @v::uuid", new { v = vip.VehicleId });
        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, vip, 10_000)));
        Assert.Equal(1, await f.CountAsync("select count(*) from alerts where type = 'VIP_CEILING_PASSED' and company_id = @c::uuid",
            new { c = company }));
    }
}
