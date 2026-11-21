using System.Text.Json;
using Zawed.Api.Modules.StationFinance.Contracts;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Fueling;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.StationFinance.FinanceTestSupport;

namespace Zawed.Tests.Modules.StationFinance;

/// <summary>
/// Station finance end to end (B6 contract; Java <c>StationFinanceTests</c>, test for test): a station is paid only to a
/// verified account, never more than is available, large payouts and reversals need a second person, held offline
/// payables wait for Operations, and the station reads its own position and nobody else's. The bank change hold is zero
/// here and the threshold 100,000 (<see cref="StationFinanceApi"/>); <c>BankAccountHoldTests</c> checks the hold itself.
/// </summary>
public sealed class StationFinanceTests(StationFinanceApi api) : IClassFixture<StationFinanceApi>, IAsyncLifetime
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
    public async Task PayoutsGoOnlyToAVerifiedAccountAndNeverExceedWhatIsAvailable()
    {
        var (pump, _, car) = await f.StationWithCustomerAsync();
        await f.FuelAsync(pump, car, 20_000); // 30,000 piastres
        var station = pump.StationId;

        var position = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/payable", ops), 200);
        Assert.Equal(30_000, position.GetProperty("balancePiastres").GetInt64());
        Assert.Equal(30_000, position.GetProperty("availablePiastres").GetInt64());

        Assert.Equal("BANK_ACCOUNT_REQUIRED", await CodeAsync(await f.PayoutAsync(ops, station, 10_000, Ref())));

        var change = await f.RequestBankChangeAsync(ops, station);
        var own = await f.AuthedAsync("POST", $"/api/v1/ops/bank-account-changes/{change}/approve", ops);
        await Http.AssertErrorAsync(own, 403, "SELF_APPROVAL_NOT_ALLOWED");
        var approved = await f.AuthedAsync("POST", $"/api/v1/ops/bank-account-changes/{change}/approve", secondOps);
        var approvedBody = await BodyAsync(approved, 200);
        Assert.Equal("ACTIVE", approvedBody.GetProperty("status").GetString());
        Assert.DoesNotContain(IbanDigits, approvedBody.GetRawText(), StringComparison.Ordinal);

        Assert.Equal("PAYOUT_EXCEEDS_AVAILABLE", await CodeAsync(await f.PayoutAsync(ops, station, 40_000, Ref())));
        var reference = Ref();
        var paid = await BodyAsync(await f.PayoutAsync(ops, station, 20_000, reference), 201);
        Assert.Equal("POSTED", paid.GetProperty("status").GetString());
        Assert.Equal(10_000, await f.StationPayableAsync(station));
        Assert.Equal("BANK_REFERENCE_IN_USE", await CodeAsync(await f.PayoutAsync(ops, station, 1_000, reference)));

        // The station manager reads the same position, without Operations' pending figure.
        var manager = await f.ManagerAsync(pump.OperatorId);
        var mine = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/station/stations/{station}/payable", manager), 200);
        Assert.Equal(10_000, mine.GetProperty("balancePiastres").GetInt64());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("pendingPayoutsPiastres").ValueKind);
        var statement = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/station/stations/{station}/statement", manager), 200);
        Assert.Equal(["DEBIT", "CREDIT"], statement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("direction").GetString()));
        var bank = await f.AuthedAsync("GET", $"/api/v1/station/stations/{station}/bank-account", manager);
        Assert.Equal("0002", (await BodyAsync(bank, 200)).GetProperty("active").GetProperty("accountLast4").GetString());
        Assert.Contains("no-store", bank.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);
        var payouts = await BodyAsync(await f.AuthedAsync("GET", "/api/v1/station/payouts", manager), 200);
        Assert.Equal([reference], payouts.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("bankReference").GetString()));

        // Another operator's manager does not learn the station exists.
        var stranger = await f.ManagerAsync(await f.Fueling.Pos.OperatorAsync());
        Assert.Equal(404, (int)(await f.AuthedAsync("GET", $"/api/v1/station/stations/{station}/payable", stranger)).StatusCode);
        var none = await BodyAsync(await f.AuthedAsync("GET", "/api/v1/station/payouts", stranger), 200);
        Assert.Empty(none.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ALargePayoutWaitsForASecondPersonAndReservesItsAmount()
    {
        var (pump, _, car) = await f.StationWithCustomerAsync(tankMl: 100_000);
        await f.FuelAsync(pump, car, 80_000);
        var station = pump.StationId; // owed 120,000
        await f.ApproveBankChangeAsync(secondOps, await f.RequestBankChangeAsync(ops, station));

        var waiting = await BodyAsync(await f.PayoutAsync(ops, station, 110_000, Ref()), 201);
        Assert.Equal("PENDING", waiting.GetProperty("status").GetString());
        Assert.True(waiting.GetProperty("approvalRequired").GetBoolean());
        Assert.Equal(120_000, await f.StationPayableAsync(station));
        // only 10,000 is left once 110,000 is reserved
        Assert.Equal("PAYOUT_EXCEEDS_AVAILABLE", await CodeAsync(await f.PayoutAsync(ops, station, 20_000, Ref())));

        var id = waiting.GetProperty("id").GetString();
        await Http.AssertErrorAsync(await f.AuthedAsync("POST", $"/api/v1/ops/payouts/{id}/approve", ops), 403, "SELF_APPROVAL_NOT_ALLOWED");
        var posted = await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/payouts/{id}/approve", secondOps), 200);
        Assert.Equal("POSTED", posted.GetProperty("status").GetString());
        Assert.Equal(10_000, await f.StationPayableAsync(station));
    }

    [Fact]
    public async Task AReversalNeedsTwoPeopleAndRefundsTheCompany()
    {
        var (pump, company, car) = await f.StationWithCustomerAsync(credit: 100_000);
        var fueling = await f.FuelAsync(pump, car, 20_000);
        Assert.Equal(70_000, await f.WalletBalanceAsync(company));

        var requested = await BodyAsync(await f.RequestReversalAsync(ops, fueling), 201);
        var id = requested.GetProperty("id").GetString();
        await Http.AssertErrorAsync(await f.AuthedAsync("POST", $"/api/v1/ops/reversals/{id}/approve", ops), 403, "SELF_APPROVAL_NOT_ALLOWED");
        var approved = await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/reversals/{id}/approve", secondOps), 200);
        Assert.Equal("APPROVED", approved.GetProperty("status").GetString());

        Assert.Equal(100_000, await f.WalletBalanceAsync(company));
        Assert.Equal(0, await f.StationPayableAsync(pump.StationId));
        Assert.Equal(409, (int)(await f.RequestReversalAsync(ops, fueling, "twice")).StatusCode);

        // The company sees the refund and why, not which staff member did it.
        var fleet = await f.FleetManagerAsync(company);
        var seen = await BodyAsync(await f.AuthedAsync("GET", "/api/v1/company/reversals", fleet), 200);
        Assert.Equal([id], seen.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("items")[0].GetProperty("requestedBy").ValueKind);
    }

    [Fact]
    public async Task AHeldOfflinePayableIsNotPaidUntilOperationsReleasesIt()
    {
        var (pump, _, car) = await f.StationWithCustomerAsync();
        var station = pump.StationId;
        await f.ApproveBankChangeAsync(secondOps, await f.RequestBankChangeAsync(ops, station));

        // 30,000 offline is past this test's exposure cap of 10,000: recorded, and its payable held.
        var offline = new OfflineTestSupport(api.Factory);
        var synced = await OfflineTestSupport.AnsweredAsync(await offline.SyncAsync(pump, await offline.CaptureAsync(pump, car, 20_000)));
        Assert.Contains("OVER_OFFLINE_ALLOWANCE", OfflineTestSupport.Conflicts(synced, 0));
        var fueling = OfflineTestSupport.FuelingId(synced, 0);

        var position = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/payable", ops), 200);
        Assert.Equal(30_000, position.GetProperty("balancePiastres").GetInt64());
        Assert.Equal(30_000, position.GetProperty("heldPiastres").GetInt64());
        Assert.Equal(0, position.GetProperty("availablePiastres").GetInt64());
        Assert.Equal("PAYOUT_EXCEEDS_AVAILABLE", await CodeAsync(await f.PayoutAsync(ops, station, 1_000, Ref())));

        var released = await f.AuthedAsync("POST", $"/api/v1/ops/fuelings/{fueling}/payable-hold/release", ops,
            """{"reason":"Checked with the station"}""");
        Assert.Equal("RELEASED", (await BodyAsync(released, 200)).GetProperty("state").GetString());
        Assert.Equal("HOLD_ALREADY_RELEASED", await CodeAsync(await f.AuthedAsync("POST",
            $"/api/v1/ops/fuelings/{fueling}/payable-hold/release", ops, """{"reason":"again"}""")));
        Assert.Equal(201, (int)(await f.PayoutAsync(ops, station, 30_000, Ref())).StatusCode);
    }

    /// <summary>
    /// QA round 1: bank-account and payout cards showed user ids. Operations now reads who asked and who decided by name
    /// and role; the station manager reads only that ZAWED operations did it.
    /// </summary>
    [Fact]
    public async Task BankChangesAndPayoutsNameTheStaffForOperationsAndOnlyZawedForTheStation()
    {
        await f.Owner.ExecuteAsync("update users set full_name = 'Hana Operations' where id = @id", new { id = ops.UserId });
        await f.Owner.ExecuteAsync("update users set full_name = 'Omar Checker' where id = @id", new { id = secondOps.UserId });
        var (pump, _, car) = await f.StationWithCustomerAsync(tankMl: 100_000);
        await f.FuelAsync(pump, car, 80_000);
        var station = pump.StationId;

        var change = await f.RequestBankChangeAsync(ops, station);
        var pending = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/bank-account-changes?stationId={station}", ops), 200);
        Assert.Equal(["Hana Operations"], pending.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("requestedByName").GetString()));
        Assert.Equal(["OPS"], pending.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("requestedByRole").GetString()));
        var approved = await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/bank-account-changes/{change}/approve", secondOps), 200);
        Assert.Equal("Omar Checker", approved.GetProperty("decidedByName").GetString());
        var details = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/bank-account", ops), 200);
        Assert.Equal("Hana Operations", details.GetProperty("active").GetProperty("requestedByName").GetString());
        Assert.Equal("Omar Checker", details.GetProperty("active").GetProperty("decidedByName").GetString());

        var waiting = await BodyAsync(await f.PayoutAsync(ops, station, 110_000, Ref()), 201);
        Assert.Equal("Hana Operations", waiting.GetProperty("requestedByName").GetString());
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("decidedByName").ValueKind);
        var id = waiting.GetProperty("id").GetString();
        var posted = await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/payouts/{id}/approve", secondOps), 200);
        Assert.Equal("Omar Checker", posted.GetProperty("decidedByName").GetString());
        Assert.Equal("OPS", posted.GetProperty("decidedByRole").GetString());
        Assert.Equal("Hana Operations",
            (await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/payouts/{id}", ops), 200)).GetProperty("requestedByName").GetString());

        var manager = await f.ManagerAsync(pump.OperatorId);
        var accountResponse = await f.AuthedAsync("GET", $"/api/v1/station/stations/{station}/bank-account", manager);
        var accountText = await accountResponse.Content.ReadAsStringAsync();
        var account = JsonDocument.Parse(accountText).RootElement.GetProperty("active");
        Assert.Equal("ZAWED_OPERATIONS", account.GetProperty("requestedByRole").GetString());
        Assert.Equal("ZAWED_OPERATIONS", account.GetProperty("decidedByRole").GetString());
        Assert.Equal(JsonValueKind.Null, account.GetProperty("requestedByName").ValueKind);
        var payoutsResponse = await f.AuthedAsync("GET", $"/api/v1/station/payouts?stationId={station}", manager);
        var payoutsText = await payoutsResponse.Content.ReadAsStringAsync();
        var payouts = JsonDocument.Parse(payoutsText).RootElement.GetProperty("items");
        Assert.Equal(["ZAWED_OPERATIONS"], payouts.EnumerateArray().Select(i => i.GetProperty("decidedByRole").GetString()));
        Assert.Equal(JsonValueKind.Null, account.GetProperty("requestedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, account.GetProperty("decidedBy").ValueKind);
        Assert.All(payouts.EnumerateArray(), p => Assert.Equal(JsonValueKind.Null, p.GetProperty("requestedBy").ValueKind));
        foreach (var forStation in new[] { accountText, payoutsText })
        {
            foreach (var secret in new[] { "Hana", "Omar", "\"OPS\"", ops.UserId.ToString(), secondOps.UserId.ToString() })
            {
                Assert.DoesNotContain(secret, forStation, StringComparison.Ordinal);
            }
        }
        // Operations keeps the ids.
        Assert.Equal(ops.UserId.ToString(), details.GetProperty("active").GetProperty("requestedBy").GetString());
    }

    /// <summary>The two events the notifications handle (3C, wave 5) are published once, on commit, with the Java fields.</summary>
    [Fact]
    public async Task APayoutIsAnnouncedOnceItPostsAndABankDecisionWhenItIsTaken()
    {
        var (pump, _, car) = await f.StationWithCustomerAsync(tankMl: 100_000);
        await f.FuelAsync(pump, car, 80_000); // 120,000
        var station = Guid.Parse(pump.StationId);
        var @operator = Guid.Parse(pump.OperatorId);

        var rejected = await f.RequestBankChangeAsync(ops, pump.StationId, "1234567890");
        await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/bank-account-changes/{rejected}/reject", secondOps,
            """{"reason":"  Letter unreadable  "}"""), 200);
        var change = await f.RequestBankChangeAsync(ops, pump.StationId);
        await f.ApproveBankChangeAsync(secondOps, change);
        var decided = api.Events.OfType<BankAccountChangeDecided>().Where(e => e.StationId == station).ToList();
        Assert.Equal([new BankAccountChangeDecided(Guid.Parse(rejected), @operator, station, false),
            new BankAccountChangeDecided(Guid.Parse(change), @operator, station, true)], decided);

        var now = await BodyAsync(await f.PayoutAsync(ops, pump.StationId, 5_000, Ref()), 201);
        var waiting = await BodyAsync(await f.PayoutAsync(ops, pump.StationId, 110_000, Ref()), 201);
        var waitingId = Guid.Parse(waiting.GetProperty("id").GetString()!);
        Assert.Equal([new PayoutPosted(Guid.Parse(now.GetProperty("id").GetString()!), @operator, station, 5_000)],
            api.Events.OfType<PayoutPosted>().Where(e => e.StationId == station));
        await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/payouts/{waitingId}/approve", secondOps), 200);
        await Http.AssertErrorAsync(await f.AuthedAsync("POST", $"/api/v1/ops/payouts/{waitingId}/approve", secondOps), 409, "PAYOUT_NOT_PENDING");
        Assert.Single(api.Events.OfType<PayoutPosted>(), e => e.PayoutId == waitingId && e.AmountPiastres == 110_000);
        Assert.Equal("Letter unreadable", await f.Owner.QueryOneAsync<string>(
            "select decision_reason from station_bank_accounts where id = @id::uuid", new { id = rejected }));
    }
}
