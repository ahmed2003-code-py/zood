using System.Text.Json;
using Zawed.Api.Modules.StationFinance.Contracts;
using Zawed.Tests.Modules.Fueling;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Modules.Network;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.StationFinance.FinanceTestSupport;

namespace Zawed.Tests.Modules.StationFinance;

/// <summary>
/// The money invariants of station finance under real concurrency (parallel HTTP requests on the real PostgreSQL, never
/// mocks): parallel approvals of a payout post once; parallel payouts never take more than is available, and one bank
/// reference pays once; a reversal raced from every side posts once and mirrors the original; held amounts are never
/// paid out; a release raced with itself is written once. After each race the cached payable agrees with the ledger.
/// </summary>
public sealed class StationFinanceConcurrencyTests(StationFinanceApi api) : IClassFixture<StationFinanceApi>, IAsyncLifetime
{
    private const int Racers = 8;

    private readonly FinanceTestSupport f = new(api.Factory);
    private SignedIn ops = null!;
    private List<SignedIn> checkers = null!;

    public async Task InitializeAsync()
    {
        ops = await f.OpsAsync();
        checkers = [];
        for (var i = 0; i < Racers; i++)
        {
            checkers.Add(await f.OpsAsync(i % 2 == 0 ? Api.Platform.Security.Role.OPS : Api.Platform.Security.Role.OPS_MANAGER));
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(string Station, string Operator, string Company, Pump Pump, Car Car)> PaidStationAsync(long volumeMl, long tankMl = 100_000)
    {
        var (pump, company, car) = await f.StationWithCustomerAsync(tankMl: tankMl);
        await f.FuelAsync(pump, car, volumeMl);
        await f.ApproveBankChangeAsync(checkers[0], await f.RequestBankChangeAsync(ops, pump.StationId));
        return (pump.StationId, pump.OperatorId, company, pump, car);
    }

    private async Task AssertPayableAgreesWithTheLedgerAsync(string station) =>
        Assert.Equal(await f.PayableLedgerAsync(station), await f.StationPayableAsync(station));

    [Fact]
    public async Task ParallelApprovalsOfOnePayoutPostItOnce()
    {
        var (station, _, _, _, _) = await PaidStationAsync(80_000); // owed 120,000
        var waiting = await BodyAsync(await f.PayoutAsync(ops, station, 110_000, Ref()), 201);
        var id = waiting.GetProperty("id").GetString()!;

        var answers = await Concurrently.RunAsync(Racers, i => f.AuthedAsync("POST", $"/api/v1/ops/payouts/{id}/approve", checkers[i]));

        Assert.Single(answers, a => (int)a.StatusCode == 200);
        foreach (var refused in answers.Where(a => (int)a.StatusCode != 200))
        {
            var error = await Http.AssertErrorAsync(refused, 409, "PAYOUT_NOT_PENDING");
            Assert.Equal("POSTED", error.GetProperty("params").GetProperty("status").GetString());
        }
        Assert.Equal(1, await f.Owner.QueryOneAsync<long>(
            "select count(*) from ledger_transactions where source_type = 'PAYOUT' and source_id = @id::uuid", new { id }));
        Assert.Equal(10_000, await f.StationPayableAsync(station));
        await AssertPayableAgreesWithTheLedgerAsync(station);
        Assert.Single(api.Events.OfType<PayoutPosted>(), e => e.PayoutId == Guid.Parse(id));
        Assert.Equal(1, await f.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where action = 'PAYOUT_APPROVED' and entity_id = @id::uuid", new { id }));
    }

    [Fact]
    public async Task ParallelPayoutsNeverTakeMoreThanIsAvailable()
    {
        var (station, _, _, _, _) = await PaidStationAsync(20_000); // owed 30,000

        var answers = await Concurrently.RunAsync(Racers, _ => f.PayoutAsync(ops, station, 7_000, Ref()));

        Assert.Equal(4, answers.Count(a => (int)a.StatusCode == 201)); // 4 × 7,000 = 28,000 of 30,000
        foreach (var refused in answers.Where(a => (int)a.StatusCode != 201))
        {
            var error = await Http.AssertErrorAsync(refused, 409, "PAYOUT_EXCEEDS_AVAILABLE");
            Assert.Equal("amountPiastres", error.GetProperty("field").GetString());
            Assert.Equal(2_000, error.GetProperty("params").GetProperty("availablePiastres").GetInt64());
            Assert.Equal(7_000, error.GetProperty("params").GetProperty("requestedPiastres").GetInt64());
        }
        Assert.Equal(2_000, await f.StationPayableAsync(station));
        await AssertPayableAgreesWithTheLedgerAsync(station);
    }

    [Fact]
    public async Task ParallelLargePayoutsReserveTheirAmountsAndNeverOverbook()
    {
        var (station, _, _, _, _) = await PaidStationAsync(100_000); // owed 150,000
        // Each one is above the threshold: recorded as PENDING, reserving its amount, nothing posted yet.
        var answers = await Concurrently.RunAsync(Racers, _ => f.PayoutAsync(ops, station, 101_000, Ref()));

        Assert.Single(answers, a => (int)a.StatusCode == 201);
        Assert.Equal(150_000, await f.StationPayableAsync(station));
        var pending = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/payable", ops), 200);
        Assert.Equal(101_000, pending.GetProperty("pendingPayoutsPiastres").GetInt64());
    }

    [Fact]
    public async Task OneBankReferencePaysOnceHoweverItIsTyped()
    {
        var (station, _, _, _, _) = await PaidStationAsync(20_000);
        var reference = "FT" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        string[] spellings = [reference, reference.ToLowerInvariant(), " " + reference[..2] + "-" + reference[2..], reference[..4] + " " + reference[4..]];

        var answers = await Concurrently.RunAsync(Racers, i => f.PayoutAsync(ops, station, 1_000, spellings[i % spellings.Length]));

        Assert.Single(answers, a => (int)a.StatusCode == 201);
        foreach (var refused in answers.Where(a => (int)a.StatusCode != 201))
        {
            var error = await Http.AssertErrorAsync(refused, 409, "BANK_REFERENCE_IN_USE");
            Assert.Equal("bankReference", error.GetProperty("field").GetString());
        }
        Assert.Equal(29_000, await f.StationPayableAsync(station));
        await AssertPayableAgreesWithTheLedgerAsync(station);

        // A cancelled payout frees its reference.
        var (other, _, _, _, _) = await PaidStationAsync(80_000);
        var large = "FT" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var waiting = await BodyAsync(await f.PayoutAsync(ops, other, 110_000, large), 201);
        await Http.AssertErrorAsync(await f.PayoutAsync(ops, other, 1_000, large.ToLowerInvariant()), 409, "BANK_REFERENCE_IN_USE");
        var cancelled = await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/payouts/{waiting.GetProperty("id").GetString()}/cancel", ops,
            """{"reason":" Wrong amount "}"""), 200);
        Assert.Equal("CANCELLED", cancelled.GetProperty("status").GetString());
        Assert.Equal("Wrong amount", cancelled.GetProperty("decisionReason").GetString());
        Assert.Equal(201, (int)(await f.PayoutAsync(ops, other, 1_000, large)).StatusCode);
    }

    [Fact]
    public async Task ARacedReversalIsRequestedOnceApprovedOnceAndMirrorsTheFueling()
    {
        var (pump, company, car) = await f.StationWithCustomerAsync(credit: 100_000);
        var fueling = await f.FuelAsync(pump, car, 20_000); // 30,000
        Assert.Equal(70_000, await f.WalletBalanceAsync(company));

        var requests = await Concurrently.RunAsync(Racers, i => f.RequestReversalAsync(checkers[i], fueling));
        Assert.Single(requests, r => (int)r.StatusCode == 201);
        foreach (var refused in requests.Where(r => (int)r.StatusCode != 201))
        {
            var error = await Http.AssertErrorAsync(refused, 409, "REVERSAL_EXISTS");
            Assert.Equal("PENDING", error.GetProperty("params").GetProperty("status").GetString());
        }
        var request = await BodyAsync(requests.Single(r => (int)r.StatusCode == 201), 201);
        var id = request.GetProperty("id").GetString()!;
        var requester = Guid.Parse(request.GetProperty("requestedBy").GetString()!);

        var approvers = checkers.Where(c => c.UserId != requester).ToList();
        var approvals = await Concurrently.RunAsync(approvers.Count, i => f.AuthedAsync("POST", $"/api/v1/ops/reversals/{id}/approve", approvers[i]));
        Assert.Single(approvals, a => (int)a.StatusCode == 200);
        foreach (var refused in approvals.Where(a => (int)a.StatusCode != 200))
        {
            await Http.AssertErrorAsync(refused, 409, "REVERSAL_NOT_PENDING");
        }

        Assert.Equal(100_000, await f.WalletBalanceAsync(company));
        Assert.Equal(0, await f.StationPayableAsync(pump.StationId));
        await AssertPayableAgreesWithTheLedgerAsync(pump.StationId);
        // The mirror: the same accounts and amounts as the fueling's posting, each on the other side.
        var mirrored = await f.Owner.QueryOneAsync<bool>("""
            with original as (select e.account_id, e.direction, e.amount_piastres from ledger_entries e
                              join fuelings f on f.ledger_transaction_id = e.transaction_id where f.id = @fueling::uuid),
                 reversal as (select e.account_id, case e.direction when 'DEBIT' then 'CREDIT' else 'DEBIT' end as direction, e.amount_piastres
                              from ledger_entries e join fueling_reversals r on r.ledger_transaction_id = e.transaction_id
                              where r.id = @id::uuid)
            select (select count(*) from original) = 2
               and not exists (select * from original except all select * from reversal)
               and not exists (select * from reversal except all select * from original)
            """, new { fueling, id });
        Assert.True(mirrored);
        Assert.Equal(1, await f.Owner.QueryOneAsync<long>(
            "select count(*) from ledger_transactions where source_type = 'REVERSAL' and source_id = @id::uuid", new { id }));
        // A second request after the approval is told it is already reversed.
        var again = await Http.AssertErrorAsync(await f.RequestReversalAsync(ops, fueling, "twice"), 409, "REVERSAL_EXISTS");
        Assert.Equal("APPROVED", again.GetProperty("params").GetProperty("status").GetString());
    }

    [Fact]
    public async Task AReversalOfAFuelingAlreadyPaidOutLeavesTheStationOwingAndNothingPayable()
    {
        var (station, _, company, pump, car) = await PaidStationAsync(20_000); // owed 30,000
        var fueling = await f.Owner.QueryOneAsync<Guid>("select id from fuelings where station_id = @s::uuid", new { s = station });
        await BodyAsync(await f.PayoutAsync(ops, station, 30_000, Ref()), 201);
        var reversal = await BodyAsync(await f.RequestReversalAsync(ops, fueling.ToString()), 201);
        await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/reversals/{reversal.GetProperty("id").GetString()}/approve", checkers[1]), 200);

        Assert.Equal(-30_000, await f.StationPayableAsync(station));
        var position = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/payable", ops), 200);
        Assert.Equal(-30_000, position.GetProperty("availablePiastres").GetInt64());
        await f.FuelAsync(pump, await f.Fueling.CarAsync(company), 10_000); // another vehicle (the tag cools down): 15,000: still owing 15,000
        await Http.AssertErrorAsync(await f.PayoutAsync(ops, station, 1, Ref()), 409, "PAYOUT_EXCEEDS_AVAILABLE");

        // The statement shows the reversal as a debit linked to the fueling it reversed, and the running balance.
        var statement = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/statement", ops), 200);
        var items = statement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["FUELING", "REVERSAL", "PAYOUT", "FUELING"], items.Select(i => i.GetProperty("sourceType").GetString()));
        Assert.Equal([-15_000L, -30_000, 0, 30_000], items.Select(i => i.GetProperty("balanceAfterPiastres").GetInt64()));
        Assert.Equal(fueling.ToString(), items[1].GetProperty("fueling").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("payout").ValueKind);
        Assert.Equal(LedgerHttp.Today.ToString("yyyy-MM-dd"), items[2].GetProperty("payout").GetProperty("valueDate").GetString());
        // The company got its 30,000 back and paid 15,000 for the second fueling.
        Assert.Equal(1_000_000 - 15_000, await f.WalletBalanceAsync(company));
    }

    [Fact]
    public async Task HeldAmountsAreNeverPaidOutAndARaceOfReleasesWritesOne()
    {
        var (station, _, company, pump, _) = await PaidStationAsync(20_000); // owed 30,000, available
        var online = await f.FuelAsync(pump, await f.Fueling.CarAsync(company), 1_000); // +1,500
        var held = await f.HeldOfflineFuelingAsync(online, 50_000);

        var position = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/payable", ops), 200);
        Assert.Equal(81_500, position.GetProperty("balancePiastres").GetInt64());
        Assert.Equal(50_000, position.GetProperty("heldPiastres").GetInt64());
        Assert.Equal(31_500, position.GetProperty("availablePiastres").GetInt64());

        // Payouts raced for more than the unheld part: together they never reach into the held 50,000.
        var payouts = await Concurrently.RunAsync(Racers, _ => f.PayoutAsync(ops, station, 9_000, Ref()));
        Assert.Equal(3, payouts.Count(p => (int)p.StatusCode == 201)); // 27,000 of 31,500
        Assert.Equal(54_500, await f.StationPayableAsync(station)); // the held 50,000 is still there
        await Http.AssertErrorAsync(await f.PayoutAsync(ops, station, 4_501, Ref()), 409, "PAYOUT_EXCEEDS_AVAILABLE");

        var holds = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/payable-holds?stationId={station}&state=HELD", ops), 200);
        var hold = Assert.Single(holds.GetProperty("items").EnumerateArray());
        Assert.Equal(held, hold.GetProperty("fuelingId").GetString());
        Assert.Equal(["OVER_OFFLINE_ALLOWANCE"], hold.GetProperty("conflicts").EnumerateArray().Select(c => c.GetString()));

        var releases = await Concurrently.RunAsync(Racers, i => f.AuthedAsync("POST", $"/api/v1/ops/fuelings/{held}/payable-hold/release",
            checkers[i], """{"reason":"Checked with the station"}"""));
        Assert.Single(releases, r => (int)r.StatusCode == 200);
        foreach (var refused in releases.Where(r => (int)r.StatusCode != 200))
        {
            await Http.AssertErrorAsync(refused, 409, "HOLD_ALREADY_RELEASED");
        }
        Assert.Equal(1, await f.Owner.QueryOneAsync<long>("select count(*) from payable_hold_releases where fueling_id = @id::uuid", new { id = held }));
        var after = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/payable", ops), 200);
        Assert.Equal(0, after.GetProperty("heldPiastres").GetInt64());
        Assert.Equal(54_500, after.GetProperty("availablePiastres").GetInt64());
        await AssertPayableAgreesWithTheLedgerAsync(station);

        // A fueling that was never held has no hold to release; an unknown one is not found.
        await Http.AssertErrorAsync(await f.AuthedAsync("POST", $"/api/v1/ops/fuelings/{online}/payable-hold/release", ops,
            """{"reason":"x"}"""), 409, "FUELING_NOT_HELD");
        await Http.AssertErrorAsync(await f.AuthedAsync("POST", $"/api/v1/ops/fuelings/{Guid.NewGuid()}/payable-hold/release", ops,
            """{"reason":"x"}"""), 404, "NOT_FOUND");
    }

    [Fact]
    public async Task AReversedHeldFuelingIsNoLongerHeldAndCannotBeReleased()
    {
        var (station, _, company, pump, _) = await PaidStationAsync(1_000);
        var online = await f.FuelAsync(pump, await f.Fueling.CarAsync(company), 1_000);
        var held = await f.HeldOfflineFuelingAsync(online, 20_000);
        var reversal = await BodyAsync(await f.RequestReversalAsync(ops, held), 201);
        await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/reversals/{reversal.GetProperty("id").GetString()}/approve", checkers[1]), 200);

        var position = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/payable", ops), 200);
        Assert.Equal(0, position.GetProperty("heldPiastres").GetInt64());
        Assert.Equal(3_000, position.GetProperty("balancePiastres").GetInt64());
        await Http.AssertErrorAsync(await f.AuthedAsync("POST", $"/api/v1/ops/fuelings/{held}/payable-hold/release", ops,
            """{"reason":"x"}"""), 409, "FUELING_REVERSED");
        var holds = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/payable-holds?stationId={station}", ops), 200);
        Assert.Equal("REVERSED", Assert.Single(holds.GetProperty("items").EnumerateArray()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task APayoutThatWaitedWhileTheAccountChangedCannotBeApproved()
    {
        var (station, _, _, _, _) = await PaidStationAsync(80_000);
        var waiting = await BodyAsync(await f.PayoutAsync(ops, station, 110_000, Ref()), 201);
        await f.ApproveBankChangeAsync(checkers[1], await f.RequestBankChangeAsync(ops, station, "123456789012"));

        await Http.AssertErrorAsync(await f.AuthedAsync("POST", $"/api/v1/ops/payouts/{waiting.GetProperty("id").GetString()}/approve", checkers[2]),
            409, "PAYOUT_BANK_ACCOUNT_CHANGED");
        Assert.Equal(120_000, await f.StationPayableAsync(station));
        var accounts = await f.Owner.QueryAsync<string>("select status from station_bank_accounts where station_id = @s::uuid order by id",
            new { s = station });
        Assert.Equal(["SUPERSEDED", "ACTIVE"], accounts);
    }

    [Fact]
    public async Task TheDatabaseRefusesADecisionByTheRequesterEvenWhenTheServiceIsBypassed()
    {
        var (station, _, _, _, _) = await PaidStationAsync(80_000);
        var waiting = await BodyAsync(await f.PayoutAsync(ops, station, 110_000, Ref()), 201);
        var change = await f.RequestBankChangeAsync(ops, station, "123456789012");
        var payoutId = Guid.Parse(waiting.GetProperty("id").GetString()!);

        // As the runtime role, acting as the requester: V9's two-person checks refuse what the service refuses. Every
        // other rule of the row is met (a real posting, decided by the acting user), so the refusal is the dual control's.
        var posting = await f.Owner.QueryOneAsync<Guid>("select ledger_transaction_id from fuelings where station_id = @s::uuid",
            new { s = station });
        var (payoutConnection, payoutTx) = await RawSql.OpenAsync(f.Owner, asApp: true, "PLATFORM", null, ops.UserId);
        await using (payoutConnection)
        {
            var refused = await RawSql.RefusedAsync(() => RawSql.ExecuteAsync(payoutConnection, payoutTx,
                $"update payouts set status = 'POSTED', decided_by = '{ops.UserId}', decided_at = now(), posted_at = now(), "
                + $"ledger_transaction_id = '{posting}' where id = '{payoutId}'"));
            Assert.Equal("payouts_two_person", refused.ConstraintName);
        }
        var (bankConnection, bankTx) = await RawSql.OpenAsync(f.Owner, asApp: true, "PLATFORM", null, ops.UserId);
        await using (bankConnection)
        {
            var refused = await RawSql.RefusedAsync(() => RawSql.ExecuteAsync(bankConnection, bankTx,
                $"update station_bank_accounts set status = 'ACTIVE', decided_by = '{ops.UserId}', decided_at = now(), "
                + $"payouts_held_until = now() where id = '{change}'"));
            Assert.Equal("station_bank_accounts_two_person", refused.ConstraintName);
        }
        Assert.Equal("PENDING", await f.Owner.QueryOneAsync<string>("select status from payouts where id = @payoutId", new { payoutId }));
    }
}
