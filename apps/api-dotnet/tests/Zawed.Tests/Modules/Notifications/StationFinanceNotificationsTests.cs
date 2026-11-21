using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.StationFinance;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Notifications.Notified;

namespace Zawed.Tests.Modules.Notifications;

/// <summary>
/// Notifications for a station's manager (B7 W5; Java <c>fueling.StationFinanceNotificationsTests</c>): a bank account
/// change decided, and a payout that posted. The operator's own manager hears of them; another operator, a company and
/// Operations do not. The settings are <c>StationFinanceTests</c>' own (<see cref="StationFinanceApi"/>).
/// </summary>
public sealed class StationFinanceNotificationsTests(StationFinanceApi api) : IClassFixture<StationFinanceApi>, IAsyncLifetime
{
    private readonly FinanceTestSupport f = new(api.Factory);
    private SignedIn ops = null!;
    private SignedIn secondOps = null!;

    public async Task InitializeAsync()
    {
        ops = await f.OpsAsync();
        secondOps = await f.OpsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TheStationManagerHearsOfTheBankAccountAndThePayoutAndNobodyElse()
    {
        var (pump, company, car) = await f.StationWithCustomerAsync();
        await f.FuelAsync(pump, car, 20_000); // owed 30,000
        var station = pump.StationId;
        var manager = await f.ManagerAsync(pump.OperatorId);
        var stranger = await f.ManagerAsync(await f.Fueling.Pos.OperatorAsync());
        var fleet = await f.FleetManagerAsync(company);

        var change = await f.RequestBankChangeAsync(ops, station);
        Assert.Equal(0, await UnreadAsync(f.Id, manager)); // a request is not a decision
        await f.ApproveBankChangeAsync(secondOps, change);
        var approved = await AboutAsync(f.Id, manager, change, "BANK_ACCOUNT_CHANGE_APPROVED");
        Assert.Equal("STATION_BANK_ACCOUNT", approved.GetProperty("entityType").GetString());
        Assert.Equal(station, Params(approved)["stationId"].GetString());

        var paid = await FinanceTestSupport.BodyAsync(await f.PayoutAsync(ops, station, 20_000, FinanceTestSupport.Ref()), 201);
        var payout = paid.GetProperty("id").GetString()!;
        var posted = await AboutAsync(f.Id, manager, payout, "PAYOUT_POSTED");
        Assert.Equal("PAYOUT", posted.GetProperty("entityType").GetString());
        Assert.Equal(station, Params(posted)["stationId"].GetString());
        Assert.Equal(20_000, Params(posted)["amountPiastres"].GetInt64());
        Assert.DoesNotContain("PO-", posted.GetRawText()); // no bank reference or account number
        Assert.DoesNotContain("2631", posted.GetRawText());

        Assert.Equal(0, await UnreadAsync(f.Id, stranger));
        Assert.Empty(await TypesAboutAsync(f.Id, fleet, payout));
        Assert.Empty(await TypesAboutAsync(f.Id, ops, payout));
        Assert.Empty(await TypesAboutAsync(f.Id, secondOps, change));
    }

    [Fact]
    public async Task ARejectedChangeIsToldAndAPayoutIsToldOnlyOnceItPosts()
    {
        var (pump, _, car) = await f.StationWithCustomerAsync(tankMl: 100_000);
        await f.FuelAsync(pump, car, 80_000);
        var station = pump.StationId; // owed 120,000
        var manager = await f.ManagerAsync(pump.OperatorId);

        var refused = await f.RequestBankChangeAsync(ops, station);
        await FinanceTestSupport.BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/bank-account-changes/{refused}/reject", secondOps,
            "{\"reason\":\"The letter is not signed\"}"), 200);
        var told = await AboutAsync(f.Id, manager, refused, "BANK_ACCOUNT_CHANGE_REJECTED");
        Assert.Equal("STATION_BANK_ACCOUNT", told.GetProperty("entityType").GetString());
        Assert.DoesNotContain("signed", told.GetRawText());

        await f.ApproveBankChangeAsync(secondOps, await f.RequestBankChangeAsync(ops, station));
        var waiting = await FinanceTestSupport.BodyAsync(await f.PayoutAsync(ops, station, 110_000, FinanceTestSupport.Ref()), 201);
        Assert.Equal("PENDING", waiting.GetProperty("status").GetString());
        var payout = waiting.GetProperty("id").GetString()!;
        Assert.Empty(await TypesAboutAsync(f.Id, manager, payout)); // waiting for a second person: nothing paid yet

        var approved = await FinanceTestSupport.BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/payouts/{payout}/approve", secondOps), 200);
        Assert.Equal("POSTED", approved.GetProperty("status").GetString());
        Assert.Equal(110_000, Params(await AboutAsync(f.Id, manager, payout, "PAYOUT_POSTED"))["amountPiastres"].GetInt64());
    }

    [Fact]
    public async Task OneDecisionTellsTheManagerOnce()
    {
        // The .NET handler list is checked against Java's: each station event is told exactly once, to the Station
        // Manager role of the operator (not to a Fleet Manager or Accountant who might share a scope id by accident).
        var (pump, _, car) = await f.StationWithCustomerAsync();
        await f.FuelAsync(pump, car, 20_000);
        var change = await f.RequestBankChangeAsync(ops, pump.StationId);
        await f.ApproveBankChangeAsync(secondOps, change);
        var rows = await f.Owner.QueryAsync<(string Scope, string Roles)>("""
            select audience_scope_type || ':' || audience_scope_id, array_to_string(audience_roles, ',')
            from notifications where entity_id = @change::uuid
            """, new { change });
        Assert.Equal([($"OPERATOR:{pump.OperatorId}", "STATION_MANAGER")], rows);
    }
}
