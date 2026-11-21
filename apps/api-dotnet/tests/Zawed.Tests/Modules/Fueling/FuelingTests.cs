using Npgsql;
using Zawed.Api.Modules.Fueling;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;

namespace Zawed.Tests.Modules.Fueling;

/// <summary>
/// The online fueling engine (plan §3.4, B4 contract §3; Java <c>FuelingTests</c>) through the real POS API: the money, every
/// decline reason in PRD order, idempotency under a race, the hard alerts, and who may read what. The concurrency tests send
/// real parallel requests to a real PostgreSQL.
/// </summary>
public sealed class FuelingTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly FuelingTestSupport f = new(api.Factory);

    // ------------------------------------------------------------------------------------------------- the money

    [Fact]
    public async Task ACompletedFuelingMovesWalletToStationPayableOnceAndStartsTheCooldown()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var car = await f.CarAsync(company);
        var txn = Guid.NewGuid();

        var done = await f.FuelAsync(pump, car, txn, 20_000, null); // 20 L × 15.00 = 300.00 EGP
        var body = await BodyAsync(done, 201);
        Assert.Contains("no-store", done.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Equal("COMPLETED", body.GetProperty("status").GetString());
        Assert.Equal(30_000, body.GetProperty("amountPiastres").GetInt64());
        Assert.Equal("AVAILABLE", body.GetProperty("walletStatus").GetString());
        Assert.Matches(@"^F-\d+$", body.GetProperty("reference").GetString());
        var text = await done.Content.ReadAsStringAsync();
        Assert.DoesNotContain("70000", text, StringComparison.Ordinal); // the agent never sees the balance
        Assert.DoesNotContain("balance", text, StringComparison.Ordinal);
        Assert.NotNull(body.GetProperty("cooldownUntil").GetString());

        Assert.Equal(70_000, await f.WalletBalanceAsync(company));
        Assert.Equal(30_000, await f.CountAsync("""
            select e.amount_piastres from ledger_entries e join ledger_accounts a on a.id = e.account_id
            where a.code = @code and e.direction = 'CREDIT'
            """, new { code = $"station:{pump.StationId}:payable" }));
        Assert.NotNull(await f.Owner.QueryOneAsync<DateTime?>("select cooldown_until from rfid_tags where vehicle_id = @v::uuid", new { v = car.VehicleId }));

        // A retry of the same clientTxnId: the stored outcome, nothing posted again.
        var again = await f.FuelAsync(pump, car, txn, 20_000, null);
        var replayed = await BodyAsync(again, 200);
        Assert.True(replayed.GetProperty("replayed").GetBoolean());
        Assert.Equal(body.GetProperty("id").GetString(), replayed.GetProperty("id").GetString());
        Assert.Equal(JsonValueKindNull, replayed.GetProperty("walletStatus").ValueKind);
        Assert.Equal(70_000, await f.WalletBalanceAsync(company));
        Assert.Equal(1, await f.CountAsync("select count(*) from fuelings where client_txn_id = @txn", new { txn }));

        // The same id for a different fueling is a client error.
        await Http.AssertErrorAsync(await f.FuelAsync(pump, car, txn, 5_000, null), 409, "IDEMPOTENCY_MISMATCH");
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task FiftyParallelRetriesOfOneFuelingRecordAndChargeItOnce()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var car = await f.CarAsync(company);
        var txn = Guid.NewGuid();

        var answers = await Concurrently.RunAsync(50, async _ =>
        {
            var response = await f.FuelAsync(pump, car, txn, 10_000, null);
            return ((int)response.StatusCode, (await BodyAsync(response, (int)response.StatusCode)).GetProperty("id").GetString());
        });
        Assert.All(answers, a => Assert.Contains(a.Item1, new[] { 200, 201 }));
        Assert.Single(answers, a => a.Item1 == 201);
        Assert.Single(answers.Select(a => a.Item2).Distinct());
        Assert.Equal(1, await f.CountAsync("select count(*) from fuelings where client_txn_id = @txn", new { txn }));
        Assert.Equal(100_000 - 15_000, await f.WalletBalanceAsync(company));
        Assert.Equal(1, await f.CountAsync("""
            select count(*) from ledger_transactions where source_type = 'FUELING'
            and source_id = (select id from fuelings where client_txn_id = @txn)
            """, new { txn }));
    }

    [Fact]
    public async Task ParallelFuelingsOfOneCompanyNeverOverdrawTheWallet()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 15_000 * 25); // exactly twenty-five 10 L fuelings
        var cars = new List<Car>();
        for (var i = 0; i < 50; i++)
        {
            cars.Add(await f.CarAsync(company));
        }
        var outcomes = await Concurrently.RunAsync(50, async i =>
        {
            var body = await BodyAsync(await f.FuelAsync(pump, cars[i], 10_000), 201);
            var status = body.GetProperty("status").GetString()!;
            return status == "COMPLETED" ? status : body.GetProperty("declineReason").GetString()!;
        });
        Assert.Equal(25, outcomes.Count(o => o == "COMPLETED"));
        Assert.Equal(25, outcomes.Count(o => o == "INSUFFICIENT_BALANCE"));
        Assert.Equal(0, await f.WalletBalanceAsync(company));
        Assert.Equal(0, await f.CountAsync("""
            select coalesce(sum(case when e.direction = 'CREDIT' then e.amount_piastres else -e.amount_piastres end), 0)::bigint
            from ledger_entries e join ledger_accounts a on a.id = e.account_id where a.code = @code
            """, new { code = $"company:{company}:wallet" }));
        // Every posting balances, each fueling posted once, and the balance_after chain of the wallet never went below zero.
        Assert.Equal(25, await f.CountAsync("""
            select count(*) from ledger_transactions where source_type = 'FUELING' and company_id = @c::uuid
            """, new { c = company }));
        Assert.Equal(0, await f.CountAsync("""
            select count(*) from ledger_entries where company_id = @c::uuid and balance_after_piastres < 0
            """, new { c = company }));
    }

    /// <summary>
    /// Beyond the Java class: parallel fuelings of one company on several devices and stations at once, each charged under the
    /// wallet lock: the wallet ends at exactly what the completed ones did not spend, and the payables add up.
    /// </summary>
    [Fact]
    public async Task ParallelFuelingsFromSeveralPumpsKeepTheWalletAndThePayablesExact()
    {
        var pumps = new List<Network.Pump>();
        for (var i = 0; i < 3; i++)
        {
            var pump = await f.PumpAsync();
            await f.PriceAsync(pump.StationId, "GAS_92", Price);
            pumps.Add(pump);
        }
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 15_000 * 10 + 7_500); // ten 10 L fuelings and half of an eleventh
        var cars = new List<Car>();
        for (var i = 0; i < 30; i++)
        {
            cars.Add(await f.CarAsync(company));
        }
        var outcomes = await Concurrently.RunAsync(30, async i =>
            (await BodyAsync(await f.FuelAsync(pumps[i % 3], cars[i], 10_000), 201)).GetProperty("status").GetString());
        Assert.Equal(10, outcomes.Count(o => o == "COMPLETED"));
        Assert.Equal(7_500, await f.WalletBalanceAsync(company));
        long payables = 0;
        foreach (var pump in pumps)
        {
            payables += await f.CountAsync("select coalesce(balance_piastres, 0) from station_payables where station_id = @s::uuid",
                new { s = pump.StationId });
        }
        Assert.Equal(150_000, payables);
    }

    // ---------------------------------------------------------------------------------------------- decline reasons

    [Fact]
    public async Task EachDeclineReasonIsRecordedWithoutMovingMoney()
    {
        var pump = await f.PumpAsync("GAS_92");
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 1_000_000);
        var ops = await f.OpsAsync();

        var cooled = await f.CarAsync(company);
        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, cooled, 1_000)));
        Assert.Equal("COOLDOWN", await ReasonAsync(await f.FuelAsync(pump, cooled, 1_000)));

        var lost = await f.CarAsync(company);
        var tag = await f.Owner.QueryOneAsync<Guid>("select id from rfid_tags where vehicle_id = @v::uuid", new { v = lost.VehicleId });
        Assert.Equal(200, (int)(await f.Id.AuthedAsync("POST", $"/api/v1/ops/tags/{tag}/status", ops,
            """{"status":"LOST","reason":"Driver lost it"}""")).StatusCode);
        Assert.Equal("TAG_INACTIVE", await ReasonAsync(await f.FuelAsync(pump, lost, 1_000)));

        var suspended = await f.CarAsync(company);
        Assert.Equal(200, (int)(await f.Id.AuthedAsync("POST", $"/api/v1/ops/vehicles/{suspended.VehicleId}/status", ops,
            """{"status":"SUSPENDED","reason":"Workshop"}""")).StatusCode);
        Assert.Equal("VEHICLE_INACTIVE", await ReasonAsync(await f.FuelAsync(pump, suspended, 1_000)));

        var wrongFuel = await f.CarAsync(company);
        Assert.Equal("FUEL_TYPE_MISMATCH", await ReasonAsync(await f.FuelAsync(pump, wrongFuel, Guid.NewGuid(), 1_000, "DIESEL")));

        var diesel = await f.CarAsync(company, "DIESEL", tankMl: 80_000);
        Assert.Equal("FUEL_NOT_AVAILABLE", await ReasonAsync(await f.FuelAsync(pump, diesel, 1_000))); // the station sells no diesel

        var limited = await f.CarAsync(company);
        Assert.Equal(200, (int)(await f.Id.AuthedAsync("PUT", $"/api/v1/ops/vehicles/{limited.VehicleId}/limits", ops,
            """{"kind":"VOLUME","period":"DAY","maxValue":5000}""")).StatusCode);
        Assert.Equal("LIMIT_EXCEEDED", await ReasonAsync(await f.FuelAsync(pump, limited, 6_000)));
        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, limited, 5_000))); // exactly the limit

        var poor = await f.CompanyAsync();
        var broke = await f.CarAsync(poor);
        var insufficient = await f.FuelAsync(pump, broke, 1_000);
        Assert.Equal("INSUFFICIENT_BALANCE", (await BodyAsync(insufficient, 201)).GetProperty("declineReason").GetString());
        Assert.Equal("INSUFFICIENT", (await BodyAsync(insufficient, 201)).GetProperty("walletStatus").GetString());

        var frozen = await f.CompanyAsync();
        await f.CreditAsync(frozen, 100_000);
        var iced = await f.CarAsync(frozen);
        Assert.Equal(200, (int)(await f.Id.AuthedAsync("POST", $"/api/v1/ops/companies/{frozen}/status", ops,
            """{"status":"FROZEN","reason":"Unpaid"}""")).StatusCode);
        var refused = await f.FuelAsync(pump, iced, 1_000);
        Assert.Equal("COMPANY_FROZEN", await ReasonAsync(refused));
        Assert.Equal("SUSPENDED", (await BodyAsync(refused, 201)).GetProperty("walletStatus").GetString());
        Assert.Equal(100_000, await f.WalletBalanceAsync(frozen));

        // Every declined row is recorded and none posted.
        var companies = new[] { Guid.Parse(company), Guid.Parse(poor), Guid.Parse(frozen) };
        Assert.Equal(0, await f.CountAsync("""
            select count(*) from fuelings where status = 'DECLINED' and company_id = any(@companies) and ledger_transaction_id is not null
            """, new { companies }));
        Assert.Equal(8, await f.CountAsync("select count(*) from fuelings where status = 'DECLINED' and company_id = any(@companies)",
            new { companies }));
    }

    /// <summary>
    /// Beyond the Java class: when several checks fail, the first in PRD §6.1 order is the reason (cooldown before tag, tag
    /// before vehicle, ..., limits before balance).
    /// </summary>
    [Fact]
    public async Task TheFirstFailingCheckInPrdOrderIsTheReason()
    {
        var pump = await f.PumpAsync("GAS_92");
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var ops = await f.OpsAsync();
        var poor = await f.CompanyAsync(); // no money: the balance check would fail too
        await f.CreditAsync(poor, 1_500);

        var car = await f.CarAsync(poor);
        Assert.Equal(200, (int)(await f.Id.AuthedAsync("PUT", $"/api/v1/ops/vehicles/{car.VehicleId}/limits", ops,
            """{"kind":"VOLUME","period":"DAY","maxValue":500}""")).StatusCode);
        Assert.Equal("LIMIT_EXCEEDED", await ReasonAsync(await f.FuelAsync(pump, car, 2_000))); // limit before balance
        Assert.Equal("FUEL_TYPE_MISMATCH", await ReasonAsync(await f.FuelAsync(pump, car, Guid.NewGuid(), 2_000, "DIESEL"))); // fuel before limit
        Assert.Equal(200, (int)(await f.Id.AuthedAsync("POST", $"/api/v1/ops/vehicles/{car.VehicleId}/status", ops,
            """{"status":"SUSPENDED","reason":"Workshop"}""")).StatusCode);
        Assert.Equal("VEHICLE_INACTIVE", await ReasonAsync(await f.FuelAsync(pump, car, Guid.NewGuid(), 2_000, "DIESEL")));

        // A cooldown comes before everything else.
        var cooled = await f.CarAsync(poor);
        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, cooled, 1_000)));
        var tag = await f.Owner.QueryOneAsync<Guid>("select id from rfid_tags where vehicle_id = @v::uuid", new { v = cooled.VehicleId });
        Assert.Equal(200, (int)(await f.Id.AuthedAsync("POST", $"/api/v1/ops/tags/{tag}/status", ops,
            """{"status":"LOST","reason":"Lost"}""")).StatusCode);
        Assert.Equal("COOLDOWN", await ReasonAsync(await f.FuelAsync(pump, cooled, Guid.NewGuid(), 50_000, "DIESEL")));
    }

    [Fact]
    public async Task AnUnknownTagIsRefusedAndNothingIsRecorded()
    {
        var pump = await f.PumpAsync();
        var txn = Guid.NewGuid();
        await Http.AssertErrorAsync(await f.Pos.PosAsync("POST", "/api/v1/pos/fuelings", pump.DeviceToken, pump.Session,
            FuelingBody(txn, RandomUid(), 1_000, null)), 404, "TAG_NOT_RECOGNISED");
        Assert.Equal(0, await f.CountAsync("select count(*) from fuelings where client_txn_id = @txn", new { txn }));
    }

    [Fact]
    public async Task TheVolumeMustBeSane()
    {
        var pump = await f.PumpAsync();
        var car = await f.CarAsync(await f.CompanyAsync());
        var tooMuch = await Http.AssertErrorAsync(await f.FuelAsync(pump, car, FuelingEngine.MaxVolumeMl + 1), 400, "INVALID_VOLUME");
        Assert.Equal("volumeMl", tooMuch.GetProperty("field").GetString());
        Assert.Equal(400, (int)(await f.FuelAsync(pump, car, 0)).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ VIP and alerts

    [Fact]
    public async Task WhileTheVipFlowIsSwitchedOffAVipVehicleFuelsLikeANormalOne()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        var vip = await f.CarAsync(company, tier: "VIP", vipCeilingMl: 30_000);

        Assert.Equal("INSUFFICIENT_BALANCE", await ReasonAsync(await f.FuelAsync(pump, vip, 10_000))); // no money, no fuel: VIP is off
        await f.CreditAsync(company, 100_000);
        var billed = await f.FuelAsync(pump, vip, 40_000);
        Assert.Equal("COMPLETED", await StatusAsync(billed));
        Assert.True((await BodyAsync(billed, 201)).GetProperty("billable").GetBoolean());
        Assert.Equal(100_000 - 60_000, await f.WalletBalanceAsync(company));
        var alerts = await BodyAsync(await f.GetAsync($"/api/v1/ops/alerts?companyId={company}&type=VIP_CEILING_PASSED", await f.OpsAsync()), 200);
        Assert.Equal(0, alerts.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task MoreThanTheTankHoldsIsApprovedButFlagged()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 1_000_000);
        var car = await f.CarAsync(company, tankMl: 40_000);

        var done = await f.FuelAsync(pump, car, 45_000);
        Assert.Equal("COMPLETED", await StatusAsync(done));
        var ops = await f.OpsAsync();
        var alerts = await BodyAsync(await f.GetAsync($"/api/v1/ops/alerts?companyId={company}", ops), 200);
        var items = alerts.GetProperty("items");
        Assert.Equal(new[] { "TANK_CAPACITY_EXCEEDED" }, items.EnumerateArray().Select(a => a.GetProperty("type").GetString()));
        var alert = items[0];
        Assert.Equal("CRITICAL", alert.GetProperty("severity").GetString());
        Assert.Equal("OPEN", alert.GetProperty("status").GetString());
        Assert.Equal((await BodyAsync(done, 201)).GetProperty("id").GetString(), alert.GetProperty("fuelingId").GetString());
        Assert.Equal(pump.DeviceId, alert.GetProperty("deviceId").GetString());
        Assert.Equal(45_000, alert.GetProperty("details").GetProperty("volumeMl").GetInt64());
        Assert.Equal(40_000, alert.GetProperty("details").GetProperty("tankCapacityMl").GetInt64());

        var manager = await f.SignInAsync(Role.FLEET_MANAGER, Scope.Company(Guid.Parse(company)));
        var mine = await BodyAsync(await f.GetAsync("/api/v1/company/alerts", manager), 200);
        Assert.Equal(new[] { "TANK_CAPACITY_EXCEEDED" }, mine.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("type").GetString()));

        var id = alert.GetProperty("id").GetString();
        var acknowledged = await BodyAsync(await f.Id.AuthedAsync("POST", $"/api/v1/ops/alerts/{id}/acknowledge", ops), 200);
        Assert.Equal("ACKNOWLEDGED", acknowledged.GetProperty("status").GetString());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, acknowledged.GetProperty("acknowledgedAt").ValueKind);
        Assert.Equal(1, await f.CountAsync("select count(*) from audit_log where action = 'ALERT_ACKNOWLEDGED' and entity_id = @id::uuid",
            new { id }));
        // Twice is a no-op, and the company cannot acknowledge.
        Assert.Equal("ACKNOWLEDGED", (await BodyAsync(await f.Id.AuthedAsync("POST", $"/api/v1/ops/alerts/{id}/acknowledge", ops), 200))
            .GetProperty("status").GetString());
        Assert.Equal(1, await f.CountAsync("select count(*) from audit_log where action = 'ALERT_ACKNOWLEDGED' and entity_id = @id::uuid",
            new { id }));
        Assert.Equal(403, (int)(await f.Id.AuthedAsync("POST", $"/api/v1/ops/alerts/{id}/acknowledge", manager)).StatusCode);
        await Http.AssertErrorAsync(await f.Id.AuthedAsync("POST", $"/api/v1/ops/alerts/{Guid.NewGuid()}/acknowledge", ops), 404, "NOT_FOUND");
    }

    [Fact]
    public async Task AFuelingThatCrossesTheLowBalanceThresholdRaisesOneAlert()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync(50_000);
        await f.CreditAsync(company, 60_000);
        var first = await f.CarAsync(company);
        var second = await f.CarAsync(company);

        var low = await f.FuelAsync(pump, first, 10_000); // 60,000 → 45,000: below 50,000
        Assert.Equal("LOW", (await BodyAsync(low, 201)).GetProperty("walletStatus").GetString());
        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, second, 10_000))); // already low: no second alert
        var alerts = await BodyAsync(await f.GetAsync($"/api/v1/ops/alerts?companyId={company}&type=LOW_BALANCE", await f.OpsAsync()), 200);
        Assert.Equal(1, alerts.GetProperty("items").GetArrayLength());
        var details = alerts.GetProperty("items")[0].GetProperty("details");
        Assert.Equal(45_000, details.GetProperty("balancePiastres").GetInt64());
        Assert.Equal(50_000, details.GetProperty("thresholdPiastres").GetInt64());
        Assert.True(alerts.GetProperty("items")[0].GetProperty("fuelingId").ValueKind == System.Text.Json.JsonValueKind.Null);
    }

    // --------------------------------------------------------------------------------------------- who reads what

    [Fact]
    public async Task EachAudienceSeesOnlyItsOwnFuelings()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var mine = await f.CompanyAsync();
        var theirs = await f.CompanyAsync();
        await f.CreditAsync(mine, 100_000);
        await f.CreditAsync(theirs, 100_000);
        var myFueling = (await BodyAsync(await f.FuelAsync(pump, await f.CarAsync(mine), 1_000), 201)).GetProperty("id").GetString();
        var theirFueling = (await BodyAsync(await f.FuelAsync(pump, await f.CarAsync(theirs), 1_000), 201)).GetProperty("id").GetString();

        var accountant = await f.SignInAsync(Role.ACCOUNTANT, Scope.Company(Guid.Parse(mine)));
        var seen = await BodyAsync(await f.GetAsync("/api/v1/company/fuelings", accountant), 200);
        Assert.Equal(new[] { myFueling }, seen.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        Assert.Equal(404, (int)(await f.GetAsync($"/api/v1/company/fuelings/{theirFueling}", accountant)).StatusCode);
        Assert.Equal(200, (int)(await f.GetAsync($"/api/v1/company/fuelings/{myFueling}", accountant)).StatusCode);

        var station = await f.SignInAsync(Role.STATION_MANAGER, new Scope(ScopeType.OPERATOR, Guid.Parse(pump.OperatorId)));
        var atStation = await BodyAsync(await f.GetAsync("/api/v1/station/fuelings", station), 200);
        Assert.Equal(new[] { myFueling, theirFueling }.Order(),
            atStation.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Order());
        var otherStation = (await f.Id.SignInAsync(await f.Id.SeedAsync(Role.STATION_MANAGER))).AccessToken;
        Assert.Equal(404, (int)(await f.GetAsync($"/api/v1/station/fuelings/{myFueling}", otherStation)).StatusCode);

        var detail = await f.GetAsync($"/api/v1/ops/fuelings/{myFueling}", await f.OpsAsync());
        Assert.Equal(200, (int)detail.StatusCode);
        var text = await detail.Content.ReadAsStringAsync();
        Assert.DoesNotContain("tagId", text, StringComparison.Ordinal); // no tag id or UID anywhere
        Assert.DoesNotContain("tagUid", text, StringComparison.Ordinal);
    }

    /// <summary>Beyond the Java class: the list filters, the cursor, and the RLS layer underneath the portal filters.</summary>
    [Fact]
    public async Task ListsFilterPageAndRowLevelSecurityHidesOtherTenants()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await BodyAsync(await f.FuelAsync(pump, await f.CarAsync(company), 1_000), 201)).GetProperty("id").GetString()!);
        }
        var declined = (await BodyAsync(await f.FuelAsync(pump, await f.CarAsync(await f.CompanyAsync()), 1_000), 201)).GetProperty("id").GetString();
        var ops = await f.OpsAsync();

        var page1 = await BodyAsync(await f.GetAsync($"/api/v1/ops/fuelings?companyId={company}&limit=2", ops), 200);
        Assert.Equal(new[] { ids[2], ids[1] }, page1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        var cursor = page1.GetProperty("nextCursor").GetString();
        var page2 = await BodyAsync(await f.GetAsync($"/api/v1/ops/fuelings?companyId={company}&limit=2&cursor={cursor}", ops), 200);
        Assert.Equal(new[] { ids[0] }, page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);

        var declinedAtStation = await BodyAsync(await f.GetAsync($"/api/v1/ops/fuelings?stationId={pump.StationId}&status=DECLINED", ops), 200);
        Assert.Equal(new[] { declined }, declinedAtStation.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        var future = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, (await BodyAsync(await f.GetAsync($"/api/v1/ops/fuelings?stationId={pump.StationId}&from={future}", ops), 200))
            .GetProperty("items").GetArrayLength());
        await Http.AssertErrorAsync(await f.GetAsync("/api/v1/ops/fuelings?cursor=nonsense", ops), 400, "INVALID_CURSOR");

        // Row-level security as the runtime role: a company sees its own rows only, whatever the query says.
        var visible = await f.Owner.AsRuntimeRoleAsync("COMPANY", Guid.Parse(company), null, async (c, tx) =>
            await Dapper.SqlMapper.QuerySingleAsync<long>(c, "select count(*) from fuelings where station_id = @s::uuid",
                new { s = pump.StationId }, tx));
        Assert.Equal(3, visible);
    }

    [Fact]
    public async Task TheRuntimeRoleCannotRewriteAFueling()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var id = (await BodyAsync(await f.FuelAsync(pump, await f.CarAsync(company), 1_000), 201)).GetProperty("id").GetString();
        var refused = await Assert.ThrowsAsync<PostgresException>(() => f.Owner.AsRuntimeRoleAsync("PLATFORM", null, null, async (c, tx) =>
            await Dapper.SqlMapper.ExecuteAsync(c, "update fuelings set volume_ml = 1 where id = @id::uuid", new { id }, tx)));
        Assert.Contains("permission denied", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneClientTxnIdRacedOnTwoCompaniesRecordsOneFueling()
    {
        // Two wallets, so the second request does not wait behind the first: it reaches the unique index and must fall
        // back to the stored outcome, which is for another tag.
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var first = await f.CompanyAsync();
        var second = await f.CompanyAsync();
        await f.CreditAsync(first, 100_000);
        await f.CreditAsync(second, 100_000);
        var cars = new[] { await f.CarAsync(first), await f.CarAsync(second) };
        var txn = Guid.NewGuid();
        var statuses = await Concurrently.RunAsync(2, async i => (int)(await f.FuelAsync(pump, cars[i], txn, 10_000, null)).StatusCode);
        Assert.Equal(new[] { 201, 409 }, statuses.Order());
        Assert.Equal(1, await f.CountAsync("select count(*) from fuelings where client_txn_id = @txn", new { txn }));
        Assert.Equal(200_000 - 15_000, await f.WalletBalanceAsync(first) + await f.WalletBalanceAsync(second));
        Assert.Equal(1, await f.CountAsync("""
            select count(*) from ledger_transactions where source_type = 'FUELING' and company_id = any(@c)
            """, new { c = new[] { Guid.Parse(first), Guid.Parse(second) } }));
    }

    /// <summary>
    /// Beyond the Java class: the same id raced many times across two companies. Exactly one fueling and one posting exist;
    /// every other answer is the stored outcome (200) or, for the other tag, IDEMPOTENCY_MISMATCH: never a 500.
    /// </summary>
    [Fact]
    public async Task ManyRacesOfOneClientTxnIdOnTwoCompaniesNeverFailOrDoublePost()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var first = await f.CompanyAsync();
        var second = await f.CompanyAsync();
        await f.CreditAsync(first, 100_000);
        await f.CreditAsync(second, 100_000);
        var cars = new[] { await f.CarAsync(first), await f.CarAsync(second) };
        var txn = Guid.NewGuid();
        var answers = await Concurrently.RunAsync(20, async i =>
        {
            var response = await f.FuelAsync(pump, cars[i % 2], txn, 10_000, null);
            return ((int)response.StatusCode, (int)response.StatusCode == 409 ? await CodeAsync(response) : null);
        });
        Assert.Single(answers, a => a.Item1 == 201);
        Assert.All(answers, a => Assert.True(a.Item1 is 200 or 201 || a is (409, "IDEMPOTENCY_MISMATCH"), $"{a}"));
        Assert.Equal(1, await f.CountAsync("select count(*) from fuelings where client_txn_id = @txn", new { txn }));
        Assert.Equal(200_000 - 15_000, await f.WalletBalanceAsync(first) + await f.WalletBalanceAsync(second));
    }

    [Fact]
    public async Task TwoFuelingsOfOneTagAtOnceGiveOneCompletedAndOneCooldown()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var car = await f.CarAsync(company);
        var outcomes = await Concurrently.RunAsync(2, async _ =>
        {
            var body = await BodyAsync(await f.FuelAsync(pump, car, 10_000), 201);
            var status = body.GetProperty("status").GetString()!;
            return status == "COMPLETED" ? status : body.GetProperty("declineReason").GetString()!;
        });
        Assert.Equal(new[] { "COMPLETED", "COOLDOWN" }, outcomes.Order());
        Assert.Equal(85_000, await f.WalletBalanceAsync(company));
    }

    /// <summary>Beyond the Java class: ten fuelings of one tag at once, from two devices: one completes, nine wait out the cooldown.</summary>
    [Fact]
    public async Task TheCooldownHoldsUnderManyParallelFuelingsOfOneTag()
    {
        var pump = await f.PumpAsync();
        var second = await f.Pos.PumpAtAsync(pump.OperatorId, pump.StationId);
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 1_000_000);
        var car = await f.CarAsync(company);
        var outcomes = await Concurrently.RunAsync(10, async i =>
        {
            var body = await BodyAsync(await f.FuelAsync(i % 2 == 0 ? pump : second, car, 10_000), 201);
            return body.GetProperty("status").GetString() == "COMPLETED" ? "COMPLETED" : body.GetProperty("declineReason").GetString()!;
        });
        Assert.Equal(1, outcomes.Count(o => o == "COMPLETED"));
        Assert.Equal(9, outcomes.Count(o => o == "COOLDOWN"));
        Assert.Equal(1_000_000 - 15_000, await f.WalletBalanceAsync(company));
    }

    [Fact]
    public async Task SuspendingTheOperatorStopsItsPumps()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var car = await f.CarAsync(company);
        Assert.Equal(200, (int)(await f.Id.AuthedAsync("POST", $"/api/v1/ops/operators/{pump.OperatorId}/status", await f.OpsAsync(),
            """{"status":"SUSPENDED","reason":"Contract ended"}""")).StatusCode);
        await Http.AssertErrorAsync(await f.FuelAsync(pump, car, 10_000), 401, "DEVICE_UNAUTHENTICATED");
        Assert.Equal(100_000, await f.WalletBalanceAsync(company));
        await Http.AssertErrorAsync(await f.Pos.TokenRequestAsync(pump.DeviceId, pump.Keys, Network.PosTestSupport.Now(),
            Network.PosTestSupport.Nonce()), 401, "DEVICE_UNAUTHENTICATED");
    }

    /// <summary>Beyond the Java class: a suspended station (its operator still active) answers 409 STATION_NOT_ACTIVE and records nothing.</summary>
    [Fact]
    public async Task AStationThatIsNotOpenRecordsNothing()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var car = await f.CarAsync(company);
        await f.Owner.ExecuteAsync("update stations set status = 'SUSPENDED' where id = @id::uuid", new { id = pump.StationId });
        var txn = Guid.NewGuid();
        await Http.AssertErrorAsync(await f.FuelAsync(pump, car, txn, 10_000, null), 409, "STATION_NOT_ACTIVE");
        Assert.Equal(0, await f.CountAsync("select count(*) from fuelings where client_txn_id = @txn", new { txn }));
        Assert.Equal(100_000, await f.WalletBalanceAsync(company));
    }

    [Fact]
    public async Task AFuelingRowCannotBeChangedEvenByTheOwner()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var id = (await BodyAsync(await f.FuelAsync(pump, await f.CarAsync(company), 1_000), 201)).GetProperty("id").GetString();
        Assert.Contains("immutable", (await Assert.ThrowsAsync<PostgresException>(() =>
            f.Owner.ExecuteAsync("update fuelings set volume_ml = 1 where id = @id::uuid", new { id }))).Message, StringComparison.Ordinal);
        Assert.Contains("immutable", (await Assert.ThrowsAsync<PostgresException>(() =>
            f.Owner.ExecuteAsync("delete from fuelings where id = @id::uuid", new { id }))).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One fueling is one transaction: when the last write of the attempt fails (here the hard alert, refused by a trigger
    /// added for the test), the posting, the balance, the fueling row and the tag's cooldown are all rolled back.
    /// </summary>
    [Fact]
    public async Task AFuelingThatFailsAtItsLastWriteLeavesNothingBehind()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var car = await f.CarAsync(company, tankMl: 10_000); // 20 L into a 10 L tank: a TANK_CAPACITY_EXCEEDED alert is the last write
        var trigger = "fueling_test_refuse_" + Guid.Parse(company).ToString("N")[..12];
        await f.Owner.ExecuteAsync($"""
            create function {trigger}() returns trigger language plpgsql as $$
            begin raise exception 'refused for the test'; end $$;
            create trigger {trigger} before insert on alerts
              for each row when (new.company_id = '{company}'::uuid) execute function {trigger}();
            """);
        try
        {
            var txn = Guid.NewGuid();
            Assert.Equal(500, (int)(await f.FuelAsync(pump, car, txn, 20_000, null)).StatusCode);
            Assert.Equal(100_000, await f.WalletBalanceAsync(company));
            Assert.Equal(0, await f.CountAsync("select count(*) from fuelings where client_txn_id = @txn", new { txn }));
            Assert.Equal(0, await f.CountAsync("select count(*) from ledger_transactions where source_type = 'FUELING' and company_id = @c::uuid",
                new { c = company }));
            Assert.Equal(0, await f.CountAsync("select coalesce(sum(balance_piastres), 0)::bigint from station_payables where station_id = @s::uuid",
                new { s = pump.StationId }));
            Assert.Null(await f.Owner.QueryOneAsync<DateTime?>("select cooldown_until from rfid_tags where vehicle_id = @v::uuid",
                new { v = car.VehicleId }));
        }
        finally
        {
            await f.Owner.ExecuteAsync($"drop trigger {trigger} on alerts; drop function {trigger}();");
        }
        // The same fueling sent again once the fault is gone is decided afresh: nothing was stored under its id.
        var retried = Guid.NewGuid();
        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, car, retried, 20_000, null)));
        Assert.Equal(70_000, await f.WalletBalanceAsync(company));
    }

    // ---------------------------------------------------------------------------------------------------- receipts

    [Fact]
    public async Task AReceiptLinkOpensOnlyWithItsSignature()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var done = await BodyAsync(await f.FuelAsync(pump, await f.CarAsync(company), 10_000), 201);
        var path = done.GetProperty("receiptPath").GetString()!;

        var receipt = await f.Id.GetAsync(path);
        var body = await BodyAsync(receipt, 200);
        Assert.Contains("no-store", receipt.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Equal(done.GetProperty("reference").GetString(), body.GetProperty("reference").GetString());
        var text = await receipt.Content.ReadAsStringAsync();
        Assert.DoesNotContain(company, text, StringComparison.Ordinal);
        Assert.DoesNotContain(pump.DeviceId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(pump.WorkerId, text, StringComparison.Ordinal);
        Assert.EndsWith(pump.WorkerId[^3..], body.GetProperty("agentWorkerId").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("•", body.GetProperty("agentWorkerId").GetString(), StringComparison.Ordinal);

        var id = done.GetProperty("id").GetString();
        await Http.AssertErrorAsync(await f.Id.GetAsync($"/api/v1/receipts/{id}"), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await f.Id.GetAsync($"/api/v1/receipts/{id}?sig=forged"), 404, "NOT_FOUND");
        var sig = path[(path.IndexOf("sig=", StringComparison.Ordinal) + 4)..];
        await Http.AssertErrorAsync(await f.Id.GetAsync($"/api/v1/receipts/{Guid.NewGuid()}?sig={sig}"), 404, "NOT_FOUND");
    }

    /// <summary>Beyond the Java class: the anonymous receipt read is limited to 30 a minute per address.</summary>
    [Fact]
    public async Task ReceiptReadsAreRateLimited()
    {
        f.Id.Limiter.Reset();
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(404, (int)(await f.Id.GetAsync($"/api/v1/receipts/{Guid.NewGuid()}?sig=x")).StatusCode);
        }
        await Http.AssertErrorAsync(await f.Id.GetAsync($"/api/v1/receipts/{Guid.NewGuid()}?sig=x"), 429, "RATE_LIMITED");
        f.Id.Limiter.Reset();
    }

    // ------------------------------------------------------------------------------------------------- resolution

    /// <summary>The resolve half of Java <c>OfflineSyncTests.anOnlineFuelingWhoseAnswerWasLostIsResolvedAndAMismatchIsReportedOnce</c>.</summary>
    [Fact]
    public async Task AnOnlineFuelingWhoseAnswerWasLostIsResolvedAndAMismatchIsReportedOnce()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var charged = Guid.NewGuid();
        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, await f.CarAsync(company), charged, 10_000, null)));

        // The terminal never saw the answer and told the agent the fueling was refused.
        const string told = """{"toldOutcome":"REFUSED"}""";
        for (var i = 0; i < 2; i++)
        {
            var resolved = await f.Pos.PosAsync("POST", $"/api/v1/pos/fuelings/{charged}/resolve", pump.DeviceToken, pump.Session, told);
            var body = await BodyAsync(resolved, 200);
            Assert.Equal("COMPLETED", body.GetProperty("status").GetString());
            Assert.True(body.GetProperty("replayed").GetBoolean());
            Assert.Contains("no-store", resolved.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(1, await f.CountAsync("select count(*) from alerts where type = 'OUTCOME_MISMATCH' and company_id = @c::uuid",
            new { c = company })); // reported once, however often the terminal asks

        // Never received, or another device's fueling: not found.
        await Http.AssertErrorAsync(await f.Pos.PosAsync("POST", $"/api/v1/pos/fuelings/{Guid.NewGuid()}/resolve", pump.DeviceToken,
            pump.Session, told), 404, "FUELING_NOT_FOUND");
        var other = await f.PumpAsync();
        Assert.Equal(404, (int)(await f.Pos.PosAsync("POST", $"/api/v1/pos/fuelings/{charged}/resolve", other.DeviceToken, other.Session,
            told)).StatusCode);
        // Told it completed: no alert.
        Assert.Equal(200, (int)(await f.Pos.PosAsync("POST", $"/api/v1/pos/fuelings/{charged}/resolve", pump.DeviceToken, pump.Session,
            """{"toldOutcome":"COMPLETED"}""")).StatusCode);
        Assert.Equal(400, (int)(await f.Pos.PosAsync("POST", $"/api/v1/pos/fuelings/{charged}/resolve", pump.DeviceToken, pump.Session,
            """{"toldOutcome":"MAYBE"}""")).StatusCode);
    }

    /// <summary>Beyond the Java class: a clientTxnId another device used is CLIENT_TXN_CONFLICT, and nothing moves.</summary>
    [Fact]
    public async Task AClientTxnIdFromAnotherDeviceIsAConflict()
    {
        var pump = await f.PumpAsync();
        var other = await f.Pos.PumpAtAsync(pump.OperatorId, pump.StationId);
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var car = await f.CarAsync(company);
        var txn = Guid.NewGuid();
        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, car, txn, 10_000, null)));
        await Http.AssertErrorAsync(await f.FuelAsync(other, car, txn, 10_000, null), 409, "CLIENT_TXN_CONFLICT");
        Assert.Equal(85_000, await f.WalletBalanceAsync(company));
    }
}
