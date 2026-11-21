using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Fueling;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;
using static Zawed.Tests.Modules.Notifications.Notified;

namespace Zawed.Tests.Modules.Notifications;

/// <summary>
/// Notifications caused by fuelings (B7 W5; Java <c>fueling.FuelingNotificationsTests</c>): a wallet falling below its
/// threshold tells the company's Fleet Manager and Accountant, and every hard alert tells Operations; nobody else hears of
/// either. They are written in the fueling's own transaction.
/// </summary>
public sealed class FuelingNotificationsTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly FuelingTestSupport f = new(api.Factory);

    [Fact]
    public async Task AFuelingBelowTheThresholdTellsTheCompanyAndOperationsAndNobodyElse()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync(50_000);
        await f.CreditAsync(company, 60_000);
        var car = await f.CarAsync(company);
        var ops = await OpsAsync();
        var fleet = await UserAsync(Role.FLEET_MANAGER, Scope.Company(Guid.Parse(company)));
        var accountant = await UserAsync(Role.ACCOUNTANT, Scope.Company(Guid.Parse(company)));
        var otherFleet = await UserAsync(Role.FLEET_MANAGER, Scope.Company(Guid.Parse(await f.CompanyAsync())));
        var manager = await UserAsync(Role.STATION_MANAGER, Scope.Operator(Guid.Parse(pump.OperatorId)));

        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, car, 10_000))); // 60,000 → 45,000: below 50,000

        foreach (var member in new[] { fleet, accountant })
        {
            var low = await AboutAsync(f.Id, member, company, "WALLET_LOW_BALANCE");
            Assert.Equal("COMPANY_WALLET", low.GetProperty("entityType").GetString());
            var values = Params(low);
            Assert.Equal(45_000, values["balancePiastres"].GetInt64());
            Assert.Equal(50_000, values["thresholdPiastres"].GetInt64());
            Assert.True(values.ContainsKey("transactionId"));
        }
        var alert = await AlertAsync(ops, company, "LOW_BALANCE");
        var raised = await AboutAsync(f.Id, ops, alert, "HARD_ALERT_RAISED");
        Assert.Equal("ALERT", raised.GetProperty("entityType").GetString());
        Assert.Equal("LOW_BALANCE", Params(raised)["alertType"].GetString());
        Assert.Equal("WARNING", Params(raised)["severity"].GetString());
        Assert.Equal(company, Params(raised)["companyId"].GetString());
        Assert.Empty(await TypesAboutAsync(f.Id, ops, company)); // a company's wallet notice is the company's
        Assert.Empty(await TypesAboutAsync(f.Id, fleet, alert)); // hard alerts are Operations' notices
        Assert.Equal(0, await UnreadAsync(f.Id, otherFleet));
        Assert.Equal(0, await UnreadAsync(f.Id, manager));

        // Already below: the next fueling crosses nothing and tells nobody again.
        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, await f.CarAsync(company), 1_000)));
        Assert.Equal(["WALLET_LOW_BALANCE"], await TypesAboutAsync(f.Id, fleet, company));
    }

    [Fact]
    public async Task MoreThanTheTankHoldsTellsOperationsOnly()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 1_000_000);
        var car = await f.CarAsync(company, tankMl: 40_000);
        var ops = await OpsAsync();
        var opsManager = await UserAsync(Role.OPS_MANAGER, null);
        var fleet = await UserAsync(Role.FLEET_MANAGER, Scope.Company(Guid.Parse(company)));

        var fueling = (await BodyAsync(await f.FuelAsync(pump, car, 45_000), 201)).GetProperty("id").GetString()!;

        var alert = await AlertAsync(ops, company, "TANK_CAPACITY_EXCEEDED");
        foreach (var operations in new[] { ops, opsManager })
        {
            var values = Params(await AboutAsync(f.Id, operations, alert, "HARD_ALERT_RAISED"));
            Assert.Equal("TANK_CAPACITY_EXCEEDED", values["alertType"].GetString());
            Assert.Equal("CRITICAL", values["severity"].GetString());
            Assert.Equal(fueling, values["fuelingId"].GetString());
            Assert.Equal(pump.StationId, values["stationId"].GetString());
        }
        Assert.Empty(await TypesAboutAsync(f.Id, fleet, alert));
    }

    // ------------------------------------------------------------------------------------------------------- helpers

    private async Task<SignedIn> OpsAsync() => new(await f.OpsAsync(), "", Guid.Empty);

    private async Task<SignedIn> UserAsync(Role role, Scope? scope)
    {
        var user = scope is null ? await f.Id.SeedAsync(role) : await f.Id.SeedAsync(role, scope, false);
        var signedIn = await f.Id.SignInAsync(user);
        f.Id.Limiter.Reset();
        return signedIn;
    }

    /// <summary>The newest alert of this type about the company, as Operations lists it.</summary>
    private async Task<string> AlertAsync(SignedIn ops, string company, string type)
    {
        var body = await BodyAsync(await f.GetAsync($"/api/v1/ops/alerts?companyId={company}&type={type}", ops.AccessToken), 200);
        return body.GetProperty("items")[0].GetProperty("id").GetString()!;
    }
}
