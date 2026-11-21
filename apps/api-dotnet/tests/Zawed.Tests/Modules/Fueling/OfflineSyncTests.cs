using System.Text.Json;
using Zawed.Api.Modules.Fueling;
using Zawed.Api.Platform.Security;
using Zawed.Domain.Rules;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Modules.Network;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;
using static Zawed.Tests.Modules.Fueling.OfflineTestSupport;

namespace Zawed.Tests.Modules.Fueling;

/// <summary>
/// Offline fuelings sent in batches (B5 contract §3 and §4; Java <c>OfflineSyncTests</c>) through the real POS API: every
/// capture is recorded once and never declined, a failing check becomes a conflict and an alert, the wallet stops at zero
/// and the rest becomes the company's debt, and an item that cannot be recorded is refused out loud without holding back
/// the others. The concurrency tests send real parallel requests to a real PostgreSQL.
/// </summary>
public sealed class OfflineSyncTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly OfflineTestSupport o = new(api.Factory);

    private FuelingTestSupport F => o.F;

    // -------------------------------------------------------------------------------------------- a batch, and again

    [Fact]
    public async Task ABatchIsRecordedOnceAndSendingItAgainChangesNothing()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 100_000);
        List<Capture> batch = [await o.CaptureAsync(pump, await F.CarAsync(company), 10_000),
            await o.CaptureAsync(pump, await F.CarAsync(company), 20_000), await o.CaptureAsync(pump, await F.CarAsync(company), 4_000)];
        var batchId = Guid.NewGuid();

        var firstResponse = await o.SyncAsync(pump, batchId, 7, batch);
        Assert.Equal(["RECORDED", "RECORDED", "RECORDED"], await ResultsAsync(firstResponse));
        Assert.Contains("no-store", firstResponse.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var first = await AnsweredAsync(firstResponse);
        Assert.Equal(batchId.ToString(), first.GetProperty("batchId").GetString());
        Assert.Equal(batch.Select(c => c.ClientTxnId.ToString()), // in the order sent
            first.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("clientTxnId").GetString()));
        Assert.Equal([15_000L, 30_000L, 6_000L],
            first.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("fueling").GetProperty("amountPiastres").GetInt64()));
        Assert.Empty(Conflicts(first, 0));
        var text = first.ToString();
        Assert.DoesNotContain("balance", text, StringComparison.Ordinal); // a device is never told a balance
        Assert.DoesNotMatch("[:,\\[]49000[,}\\]]", text); // as a JSON number only: a random plate may contain the digits

        // Each is a completed offline fueling, judged at the device's time, by the agent the item names.
        var row = await o.FuelingRowAsync(FuelingId(first, 0));
        Assert.Equal("COMPLETED", row["status"]);
        Assert.Equal("OFFLINE", row["origin"]);
        Assert.Equal(pump.WorkerId, row["agent_worker_id"]);
        Assert.Equal((DateTime)row["evaluated_at"]!, (DateTime)row["device_at"]!);
        Assert.True(Math.Abs(((DateTime)row["evaluated_at"]! - batch[0].DeviceAt.UtcDateTime).TotalMilliseconds) < 1);
        // The money: wallet to station payable, once per fueling; the cooldown starts at the device's time.
        Assert.Equal(100_000 - 51_000, await o.WalletBalanceAsync(company));
        Assert.Equal(await o.WalletBalanceAsync(company), await o.WalletLedgerAsync(company));
        Assert.Equal(51_000, await o.StationPayableAsync(pump.StationId));
        Assert.Equal(0, await o.OwedAsync(company));
        var cooldown = await o.Owner.QueryOneAsync<DateTime>("select cooldown_until from rfid_tags where id = @id", new { id = (Guid)row["tag_id"]! });
        Assert.True(Math.Abs((cooldown - batch[0].DeviceAt.UtcDateTime - TimeSpan.FromMinutes(180)).TotalSeconds) < 1);
        // The receipt link of a synced fueling opens like any other.
        Assert.Equal(200, (int)(await F.Id.GetAsync(Item(first, 0).GetProperty("fueling").GetProperty("receiptPath").GetString()!)).StatusCode);

        // The same batch again: every item is a duplicate carrying the stored fueling; nothing moves.
        var againResponse = await o.SyncAsync(pump, batchId, 7, batch);
        Assert.Equal(["DUPLICATE", "DUPLICATE", "DUPLICATE"], await ResultsAsync(againResponse));
        var again = await AnsweredAsync(againResponse);
        Assert.Equal(first.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("fueling").GetProperty("id").GetString()),
            again.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("fueling").GetProperty("id").GetString()));
        Assert.Equal(49_000, await o.WalletBalanceAsync(company));
        Assert.Equal(51_000, await o.StationPayableAsync(pump.StationId));
        Assert.Equal(3, await o.CountAsync("select count(*) from fuelings where company_id = @c::uuid", new { c = company }));
        Assert.Equal(3, await o.CountAsync("select count(*) from ledger_transactions where source_type = 'FUELING' and company_id = @c::uuid",
            new { c = company }));
        Assert.Equal(0, await o.CountAsync("select count(*) from alerts where company_id = @c::uuid or device_id = @d::uuid",
            new { c = company, d = pump.DeviceId })); // nothing to flag

        // Both requests are in the sync monitor's log, with what each did.
        var batches = await o.BatchRowsAsync(pump);
        Assert.Equal(2, batches.Count);
        Assert.Equal(batchId, batches[0]["device_batch_id"]);
        Assert.Equal(3, batches[0]["item_count"]);
        Assert.Equal(3, batches[0]["recorded"]);
        Assert.Equal(0, batches[0]["duplicates"]);
        Assert.Equal(0, batches[0]["rejected"]);
        Assert.Equal(0, batches[0]["with_conflicts"]);
        Assert.Equal(7, batches[0]["pending_reported"]);
        Assert.Equal(0, batches[1]["recorded"]);
        Assert.Equal(3, batches[1]["duplicates"]);
        Assert.Equal(pump.OperatorId, batches[1]["operator_id"]!.ToString());
    }

    [Fact]
    public async Task TheSameBatchFromTwoThreadsAtOnceRecordsEachFuelingOnce()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var first = await F.CompanyAsync();
        var second = await F.CompanyAsync();
        await F.CreditAsync(first, 1_000_000);
        await F.CreditAsync(second, 1_000_000);
        var batch = new List<Capture>();
        for (var i = 0; i < 12; i++)
        {
            batch.Add(await o.CaptureAsync(pump, await F.CarAsync(i % 3 == 0 ? second : first), 10_000));
        }
        var batchId = Guid.NewGuid();

        var answers = await Concurrently.RunAsync(4, async _ => await ResultsAsync(await o.SyncAsync(pump, batchId, 0, batch)));

        for (var item = 0; item < batch.Count; item++)
        {
            var outcomes = answers.Select(a => a[item]).Order().ToList();
            Assert.Equal(["DUPLICATE", "DUPLICATE", "DUPLICATE", "RECORDED"], outcomes);
            Assert.Equal(1, await o.CountAsync("select count(*) from fuelings where client_txn_id = @t", new { t = batch[item].ClientTxnId }));
        }
        Assert.Equal(1_000_000 - 8 * 15_000, await o.WalletBalanceAsync(first));
        Assert.Equal(1_000_000 - 4 * 15_000, await o.WalletBalanceAsync(second));
        Assert.Equal(await o.WalletBalanceAsync(first), await o.WalletLedgerAsync(first));
        Assert.Equal(12 * 15_000, await o.StationPayableAsync(pump.StationId));
        Assert.Equal(12, await o.CountAsync("""
            select count(*) from ledger_transactions t join fuelings f on f.id = t.source_id
            where t.source_type = 'FUELING' and f.device_id = @d::uuid
            """, new { d = pump.DeviceId }));
        // Four requests, four rows; between them each item was recorded exactly once.
        var rows = await o.BatchRowsAsync(pump);
        Assert.Equal(4, rows.Count);
        Assert.Equal(12, rows.Sum(r => (int)r["recorded"]!));
        Assert.Equal(36, rows.Sum(r => (int)r["duplicates"]!));
    }

    [Fact]
    public async Task TwoDevicesSyncingCapturesForOneWalletAtOnceChargeEachOnceAndNeverOverdraw()
    {
        // Beyond Java: two terminals of different stations come back online together with captures of one company, more
        // than its wallet holds. Both batches lock the same wallet; whichever order they take it, the wallet ends at
        // exactly zero, the station payables hold every capture and the rest is the company's debt.
        var a = await F.PumpAsync();
        var b = await F.PumpAsync();
        await F.PriceAsync(a.StationId, "GAS_92", Price);
        await F.PriceAsync(b.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 100_000);
        var fromA = new List<Capture>();
        var fromB = new List<Capture>();
        for (var i = 0; i < 8; i++)
        {
            fromA.Add(await o.CaptureAsync(a, await F.CarAsync(company), 10_000)); // 15,000 each
            fromB.Add(await o.CaptureAsync(b, await F.CarAsync(company), 10_000));
        }

        var answers = await Concurrently.RunAsync(4, async i =>
            await ResultsAsync(await o.SyncAsync(i % 2 == 0 ? a : b, Guid.NewGuid(), 0, i % 2 == 0 ? fromA : fromB)));

        Assert.All(answers, results => Assert.Equal(8, results.Count));
        for (var item = 0; item < 8; item++)
        {
            Assert.Equal(["DUPLICATE", "RECORDED"], new[] { answers[0][item], answers[2][item] }.Order());
            Assert.Equal(["DUPLICATE", "RECORDED"], new[] { answers[1][item], answers[3][item] }.Order());
        }
        Assert.Equal(0, await o.WalletBalanceAsync(company));
        Assert.Equal(0, await o.WalletLedgerAsync(company));
        Assert.Equal(16 * 15_000 - 100_000, await o.OwedAsync(company));
        Assert.Equal(8 * 15_000, await o.StationPayableAsync(a.StationId));
        Assert.Equal(8 * 15_000, await o.StationPayableAsync(b.StationId));
        Assert.Equal(0, await o.CountAsync("""
            select count(*) from ledger_entries e join ledger_accounts a on a.id = e.account_id
            where a.code = @code and e.balance_after_piastres < 0
            """, new { code = "company:" + company + ":wallet" }));
        Assert.Equal(16, await o.CountAsync("select count(*) from fuelings where company_id = @c::uuid and origin = 'OFFLINE'", new { c = company }));
    }

    // ------------------------------------------------------------------------------------------------- conflicts

    [Fact]
    public async Task EveryFailingCheckIsRecordedAsAConflictAndAlertedWhileTheFuelingStaysCompleted()
    {
        var pump = await F.PumpAsync("GAS_92", "DIESEL");
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        await F.PriceAsync(pump.StationId, "DIESEL", 1_200);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 10_000_000);
        var ops = await F.OpsAsync();

        // COOLDOWN: the car fuelled online a moment before the capture.
        var cooled = await F.CarAsync(company);
        Assert.Equal(201, (int)(await F.FuelAsync(pump, cooled, 1_000)).StatusCode);
        await AssertRecordedWithAsync(pump, await o.CaptureAsync(pump, cooled, 1_000), "WARNING", "COOLDOWN");

        // TAG_INACTIVE: reported lost, and still used at the pump.
        var lost = await F.CarAsync(company);
        var tag = await TagOfAsync(lost);
        Assert.Equal(200, (int)(await F.Id.AuthedAsync("POST", $"/api/v1/ops/tags/{tag}/status", ops,
            """{"status":"LOST","reason":"Driver lost it"}""")).StatusCode);
        await AssertRecordedWithAsync(pump, await o.CaptureAsync(pump, lost, 1_000), "CRITICAL", "TAG_INACTIVE");

        // VEHICLE_INACTIVE.
        var suspended = await F.CarAsync(company);
        Assert.Equal(200, (int)(await F.Id.AuthedAsync("POST", $"/api/v1/ops/vehicles/{suspended.VehicleId}/status", ops,
            """{"status":"SUSPENDED","reason":"Workshop"}""")).StatusCode);
        await AssertRecordedWithAsync(pump, await o.CaptureAsync(pump, suspended, 1_000), "CRITICAL", "VEHICLE_INACTIVE");

        // FUEL_TYPE_MISMATCH: diesel into a petrol car; charged at the price of what was dispensed.
        var wrongFuel = await AssertRecordedWithAsync(pump, (await o.CaptureAsync(pump, await F.CarAsync(company), 1_000)).WithFuel("DIESEL"),
            "WARNING", "FUEL_TYPE_MISMATCH");
        Assert.Equal(1_200, Item(wrongFuel, 0).GetProperty("fueling").GetProperty("unitPricePiastres").GetInt64());

        // LIMIT_EXCEEDED.
        var limited = await F.CarAsync(company);
        Assert.Equal(200, (int)(await F.Id.AuthedAsync("PUT", $"/api/v1/ops/vehicles/{limited.VehicleId}/limits", ops,
            """{"kind":"VOLUME","period":"DAY","maxValue":5000}""")).StatusCode);
        await AssertRecordedWithAsync(pump, await o.CaptureAsync(pump, limited, 6_000), "WARNING", "LIMIT_EXCEEDED");

        // STALE_CAPTURE: a capture that sat on the device for eight days, from a shift of that day.
        var longAgo = DateTimeOffset.UtcNow.AddDays(-8);
        await o.PastShiftAsync(pump, longAgo.AddHours(-1), longAgo.AddHours(1));
        await o.PriceSinceAsync(pump.StationId, "GAS_92", 1_400, longAgo.AddDays(-1));
        var stale = await AssertRecordedWithAsync(pump, (await o.CaptureAsync(pump, await F.CarAsync(company), 1_000)).At(longAgo), "WARNING",
            "STALE_CAPTURE");
        Assert.Equal(1_400, Item(stale, 0).GetProperty("fueling").GetProperty("unitPricePiastres").GetInt64()); // the price of that day

        // Everything above was charged in full: a conflict never stops the money.
        Assert.Equal(10_000_000 - (1_500 + 1_500 + 1_500 + 1_500 + 1_200 + 9_000 + 1_400), await o.WalletBalanceAsync(company));
        Assert.Equal(await o.WalletBalanceAsync(company), await o.WalletLedgerAsync(company));

        // FUEL_NOT_AVAILABLE: a fuel this station has no price for. Recorded with no amount, flagged.
        var petrolOnly = await F.PumpAsync("GAS_92");
        await F.PriceAsync(petrolOnly.StationId, "GAS_92", Price);
        var diesel = await F.CarAsync(company, "DIESEL", "NORMAL", null, 80_000);
        var before = await o.WalletBalanceAsync(company);
        var unpriced = await AssertRecordedWithAsync(petrolOnly, await o.CaptureAsync(petrolOnly, diesel, 1_000), "CRITICAL", "FUEL_NOT_AVAILABLE");
        Assert.Equal(0, Item(unpriced, 0).GetProperty("fueling").GetProperty("amountPiastres").GetInt64());
        Assert.Null((await o.FuelingRowAsync(FuelingId(unpriced, 0)))["ledger_transaction_id"]);
        Assert.Equal(before, await o.WalletBalanceAsync(company));

        // COMPANY_FROZEN, and several at once: all of them, in the order of the checks, in one alert.
        var frozen = await F.CompanyAsync();
        await F.CreditAsync(frozen, 100_000);
        var iced = await F.CarAsync(frozen);
        var icedTag = await TagOfAsync(iced);
        Assert.Equal(200, (int)(await F.Id.AuthedAsync("POST", $"/api/v1/ops/companies/{frozen}/status", ops,
            """{"status":"FROZEN","reason":"Unpaid"}""")).StatusCode);
        await AssertRecordedWithAsync(pump, await o.CaptureAsync(pump, iced, 1_000), "CRITICAL", "COMPANY_FROZEN");
        Assert.Equal(100_000 - 1_500, await o.WalletBalanceAsync(frozen)); // a frozen company still pays for the fuel it took
        Assert.Equal(200, (int)(await F.Id.AuthedAsync("POST", $"/api/v1/ops/tags/{icedTag}/status", ops,
            """{"status":"STOLEN","reason":"Reported"}""")).StatusCode);
        await AssertRecordedWithAsync(pump, await o.CaptureAsync(pump, iced, 2_000), "CRITICAL", "COOLDOWN", "TAG_INACTIVE", "COMPANY_FROZEN");

        // The company reads its own conflicts; the batch log counted them.
        var manager = await F.SignInAsync(Role.FLEET_MANAGER, Scope.Company(Guid.Parse(frozen)));
        var companyAlerts = await BodyAsync(await F.GetAsync("/api/v1/company/alerts", manager), 200);
        Assert.Equal(["SYNC_CONFLICT", "SYNC_CONFLICT"], companyAlerts.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("type").GetString()));
        Assert.All(await o.BatchRowsAsync(pump), batch =>
        {
            Assert.Equal(1, batch["recorded"]);
            Assert.Equal(1, batch["with_conflicts"]);
        });
        Assert.Equal(0, await o.CountAsync("select count(*) from fuelings where origin = 'OFFLINE' and status <> 'COMPLETED'"));
    }

    [Fact]
    public async Task EveryDeclineReasonHasItsConflict()
    {
        foreach (var reason in Enum.GetValues<FuelingRules.DeclineReason>())
        {
            Assert.Equal(reason.ToString(), Enum.Parse<Conflict>(reason.ToString()).ToString());
        }
        // And the database knows every one (the CHECK on fuelings.conflicts, V8).
        foreach (var conflict in Enum.GetValues<Conflict>())
        {
            Assert.True(await o.Owner.QueryOneAsync<bool>(
                "select pg_get_constraintdef(oid) like @p from pg_constraint where conname = 'fuelings_conflicts_known'",
                new { p = "%'" + conflict + "'%" }), conflict.ToString());
        }
    }

    // ------------------------------------------------------------------------------------- the wallet and the debt

    [Fact]
    public async Task WhatTheWalletCannotCoverBecomesTheCompanysDebtAndTheStationIsPaidInFull()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync(5_000);
        await F.CreditAsync(company, 10_000);

        // 20 L × 15.00 = 300.00 EGP against a wallet of 100.00 EGP.
        var short200 = await AnsweredAsync(await o.SyncAsync(pump, await o.CaptureAsync(pump, await F.CarAsync(company), 20_000)));
        Assert.Equal(["INSUFFICIENT_BALANCE"], Conflicts(short200, 0));
        var fueling = FuelingId(short200, 0);
        Assert.Equal(30_000, Item(short200, 0).GetProperty("fueling").GetProperty("amountPiastres").GetInt64());

        Assert.Equal(0, await o.WalletBalanceAsync(company)); // exactly zero, never below
        Assert.Equal(0, await o.WalletLedgerAsync(company)); // the cached balance is the ledger's
        Assert.Equal(20_000, await o.OwedAsync(company));
        Assert.Equal(30_000, await o.StationPayableAsync(pump.StationId)); // the station is paid in full
        // One balanced posting for the fueling: wallet 100 + receivable 200 = station 300.
        var txn = (Guid)(await o.FuelingRowAsync(fueling))["ledger_transaction_id"]!;
        Assert.Equal(new[] { $"CREDIT station:{pump.StationId}:payable 30000", $"DEBIT company:{company}:receivable 20000",
            $"DEBIT company:{company}:wallet 10000" }, (await o.EntriesAsync(txn)).Order());
        Assert.Equal(fueling, (await o.Owner.QueryOneAsync<Guid>("select source_id from ledger_transactions where id = @txn", new { txn })).ToString());

        // Alerted to Operations and the company with what is owed; the debt is in the audit trail; draining the wallet
        // crossed its threshold like any other debit.
        var alerts = (await BodyAsync(await F.GetAsync($"/api/v1/ops/alerts?companyId={company}", await F.OpsAsync()), 200))
            .GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["LOW_BALANCE", "SYNC_CONFLICT"], alerts.Select(a => a.GetProperty("type").GetString()).Order());
        var conflict = alerts.Single(a => a.GetProperty("type").GetString() == "SYNC_CONFLICT");
        Assert.Equal(20_000, conflict.GetProperty("details").GetProperty("owedPiastres").GetInt64());
        Assert.Equal("CRITICAL", conflict.GetProperty("severity").GetString());
        Assert.Equal("20000", await o.Owner.QueryOneAsync<string>("""
            select metadata ->> 'amountPiastres' from audit_log
            where action = 'COMPANY_RECEIVABLE_RAISED' and entity_id = @c::uuid
            """, new { c = company }));

        // With nothing left, the next capture is owed whole; the wallet row is not touched.
        var allOwed = await AnsweredAsync(await o.SyncAsync(pump, await o.CaptureAsync(pump, await F.CarAsync(company), 10_000)));
        Assert.Equal(["INSUFFICIENT_BALANCE"], Conflicts(allOwed, 0));
        Assert.Equal(0, await o.WalletBalanceAsync(company));
        Assert.Equal(35_000, await o.OwedAsync(company));
        Assert.Equal(45_000, await o.StationPayableAsync(pump.StationId));
        Assert.Equal(new[] { $"CREDIT station:{pump.StationId}:payable 15000", $"DEBIT company:{company}:receivable 15000" },
            (await o.EntriesAsync((Guid)(await o.FuelingRowAsync(FuelingId(allOwed, 0)))["ledger_transaction_id"]!)).Order());

        // A top-up fills the wallet; the debt stays where it is until someone decides how it is settled.
        await F.CreditAsync(company, 50_000);
        Assert.Equal(50_000, await o.WalletBalanceAsync(company));
        Assert.Equal(35_000, await o.OwedAsync(company));

        // The nightly comparison of wallets with their ledger still finds nothing for this company.
        var admin = await F.SignInAsync(Role.SUPER_ADMIN, Scope.Platform);
        var run = await BodyAsync(await F.Id.AuthedAsync("POST", "/api/v1/ops/reconciliation/runs", admin), 201);
        Assert.Equal(0, await o.CountAsync("select count(*) from reconciliation_findings where run_id = @r::uuid and company_id = @c::uuid",
            new { r = run.GetProperty("id").GetString(), c = company }));
    }

    [Fact]
    public async Task AnOnlineFuelingAndAnOfflineBatchAtOnceNeverOverdrawTheWallet()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        const long opening = 15_000 * 10;
        await F.CreditAsync(company, opening); // ten 10 L fuelings; twenty are coming
        var online = new List<Car>();
        var batch = new List<Capture>();
        for (var i = 0; i < 10; i++)
        {
            online.Add(await F.CarAsync(company));
            batch.Add(await o.CaptureAsync(pump, await F.CarAsync(company), 10_000));
        }

        var outcomes = await Concurrently.RunAsync(11, async i =>
        {
            if (i == 0)
            {
                Assert.All(await ResultsAsync(await o.SyncAsync(pump, Guid.NewGuid(), 0, batch)), r => Assert.Equal("RECORDED", r));
                return "BATCH";
            }
            var body = await BodyAsync(await F.FuelAsync(pump, online[i - 1], 10_000), 201);
            var status = body.GetProperty("status").GetString()!;
            return status == "COMPLETED" ? status : body.GetProperty("declineReason").GetString()!;
        });

        var completedOnline = outcomes.Count(x => x == "COMPLETED");
        // Which side wins the race decides how many online fuelings are declined; whatever is declined is declined for the
        // balance, never owed.
        Assert.All(outcomes.Where(x => x is not "BATCH" and not "COMPLETED"), x => Assert.Equal("INSUFFICIENT_BALANCE", x));
        var charged = 15_000L * (10 + completedOnline);

        var wallet = await o.WalletBalanceAsync(company);
        Assert.True(wallet >= 0);
        Assert.Equal(wallet, await o.WalletLedgerAsync(company)); // the cached balance is the ledger's
        Assert.Equal(charged, await o.StationPayableAsync(pump.StationId)); // every recorded fueling is paid to the station
        Assert.Equal(charged, opening - wallet + await o.OwedAsync(company)); // paid from the wallet plus owed = charged
        Assert.Equal(Math.Max(0, charged - opening), await o.OwedAsync(company)); // a debt arises only once the wallet is empty
        // The running balance on the wallet's entries never dipped below zero on the way.
        Assert.Equal(0, await o.CountAsync("""
            select count(*) from ledger_entries e join ledger_accounts a on a.id = e.account_id
            where a.code = @code and e.balance_after_piastres < 0
            """, new { code = "company:" + company + ":wallet" }));
        Assert.Equal(10, await o.CountAsync(
            "select count(*) from fuelings where company_id = @c::uuid and origin = 'OFFLINE' and status = 'COMPLETED'", new { c = company }));
    }

    // -------------------------------------------------------------------------------------- judged at its own time

    [Fact]
    public async Task ACaptureIsJudgedAtItsOwnTimeNotAtTheTimeItArrives()
    {
        var pump = await F.PumpAsync();
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 1_000_000);
        var car = await F.CarAsync(company);
        Assert.Equal(200, (int)(await F.Id.AuthedAsync("PUT", $"/api/v1/ops/vehicles/{car.VehicleId}/limits", await F.OpsAsync(),
            """{"kind":"VOLUME","period":"DAY","maxValue":10000}""")).StatusCode);
        // Yesterday in the station's zone: a shift from 09:00 to 16:00, at yesterday's price.
        var cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
        var yesterday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, cairo).DateTime).AddDays(-1);
        DateTimeOffset InCairo(int hour)
        {
            var local = yesterday.ToDateTime(new TimeOnly(hour, 0));
            return new DateTimeOffset(local, cairo.GetUtcOffset(local)).ToUniversalTime();
        }
        var morning = InCairo(10);
        var afternoon = InCairo(15);
        await o.PastShiftAsync(pump, morning.AddHours(-1), afternoon.AddHours(1));
        await o.PriceSinceAsync(pump.StationId, "GAS_92", 1_000, morning.AddDays(-30));
        await F.PriceAsync(pump.StationId, "GAS_92", Price); // today's price, in force from now

        // Today the car fuels online: 8 L of its 10 L a day, at today's price.
        var today = await BodyAsync(await F.FuelAsync(pump, car, 8_000), 201);
        Assert.Equal("COMPLETED", today.GetProperty("status").GetString());
        var cooldownToday = DateTimeOffset.Parse(today.GetProperty("cooldownUntil").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        // Yesterday morning's 8 L arrives now. It is counted in yesterday, priced at yesterday's price, and is not in today's
        // cooldown: no conflict, although today already holds 8 L.
        var first = await AnsweredAsync(await o.SyncAsync(pump, (await o.CaptureAsync(pump, car, 8_000)).At(morning)));
        Assert.Empty(Conflicts(first, 0));
        Assert.Equal(1_000, Item(first, 0).GetProperty("fueling").GetProperty("unitPricePiastres").GetInt64());
        Assert.Equal(8_000, Item(first, 0).GetProperty("fueling").GetProperty("amountPiastres").GetInt64());

        // Yesterday afternoon's 5 L makes yesterday 13 L: over yesterday's limit, and only that.
        var second = await AnsweredAsync(await o.SyncAsync(pump, (await o.CaptureAsync(pump, car, 5_000)).At(afternoon)));
        Assert.Equal(["LIMIT_EXCEEDED"], Conflicts(second, 0));

        // A capture two hours after the morning one is inside its cooldown, whichever arrived last.
        var tooSoon = await AnsweredAsync(await o.SyncAsync(pump, (await o.CaptureAsync(pump, car, 1_000)).At(morning.AddHours(2))));
        Assert.Equal(["COOLDOWN", "LIMIT_EXCEEDED"], Conflicts(tooSoon, 0));

        // Old captures never shorten the cooldown today's fueling started.
        var cooldown = await o.Owner.QueryOneAsync<DateTime>("select cooldown_until from rfid_tags where vehicle_id = @v::uuid", new { v = car.VehicleId });
        Assert.True(Math.Abs((cooldown - cooldownToday.UtcDateTime).TotalMilliseconds) < 1);
        Assert.Equal(1_000_000 - 12_000 - 8_000 - 5_000 - 1_000, await o.WalletBalanceAsync(company));
    }

    // ------------------------------------------------------------------------------------------------ rejections

    [Fact]
    public async Task ACaptureCountsOnlyForAnAgentWhoWasSignedInOnThatDeviceThen()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 100_000);
        var car = await F.CarAsync(company);

        // An agent of another station; an agent of this station who never signed in on this device.
        var elsewhere = await F.PumpAsync();
        var colleague = await F.Pos.AgentAsync(pump.StationId);
        var colleagueId = (await o.Owner.QueryOneAsync<Guid>("select id from agents where worker_id = @w", new { w = colleague.WorkerId })).ToString();
        // The right agent, but an hour before the shift began.
        var refused = await AnsweredAsync(await o.SyncAsync(pump, (await o.CaptureAsync(pump, car, 1_000)).By(await o.AgentIdAsync(elsewhere)),
            (await o.CaptureAsync(pump, car, 1_000)).By(colleagueId), (await o.CaptureAsync(pump, car, 1_000)).By(Guid.NewGuid().ToString()),
            (await o.CaptureAsync(pump, car, 1_000)).At(DateTimeOffset.UtcNow.AddHours(-1))));
        // Not of this station (another station's agent, an unknown id): nothing can be recorded. Of this station but with no
        // session on this device then: recorded, flagged AGENT_UNVERIFIED (B5 review 1).
        Assert.Equal(["REJECTED", "RECORDED", "REJECTED", "RECORDED"],
            refused.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("result").GetString()));
        Assert.Equal("AGENT_NOT_OF_STATION", Rejection(refused, 0));
        Assert.Equal("AGENT_NOT_OF_STATION", Rejection(refused, 2));
        Assert.Contains("AGENT_UNVERIFIED", Conflicts(refused, 1));
        Assert.Contains("AGENT_UNVERIFIED", Conflicts(refused, 3));
        Assert.Equal(2, await o.CountAsync("select count(*) from fuelings where company_id = @c::uuid", new { c = company }));
        Assert.Equal(2, await o.CountAsync("select count(*) from alerts where type = 'SYNC_REJECTED' and device_id = @d::uuid", new { d = pump.DeviceId }));

        // A shift that ended an hour ago: what was captured in it counts, what is dated after it does not.
        var now = DateTimeOffset.UtcNow;
        await o.Owner.ExecuteAsync("""
            update agent_sessions set created_at = @from, revoked_at = @to, revoke_reason = 'SIGNED_OUT'
            where device_id = @d::uuid and revoked_at is null
            """, new { from = now.AddHours(-3).UtcDateTime, to = now.AddHours(-1).UtcDateTime, d = pump.DeviceId });
        await o.PriceSinceAsync(pump.StationId, "GAS_92", 1_300, now.AddDays(-1));
        var during = (await o.CaptureAsync(pump, car, 2_000)).At(now.AddHours(-2));
        var after = (await o.CaptureAsync(pump, await F.CarAsync(company), 2_000)).At(now.AddMinutes(-30));
        // Sent while a colleague is signed in on the device: the capture is still its own agent's.
        var colleagueSession = (await PosTestSupport.Json(await F.Pos.AgentLoginAsync(pump.DeviceToken, colleague.WorkerId, colleague.Pin), 200))
            ["sessionToken"]!.GetValue<string>();
        var mixed = await AnsweredAsync(await F.Pos.PosAsync("POST", "/api/v1/pos/sync", pump.DeviceToken, colleagueSession,
            BatchBody(Guid.NewGuid(), 0, [during, after])));
        Assert.Equal(["RECORDED", "RECORDED"], mixed.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("result").GetString()));
        Assert.Contains("AGENT_UNVERIFIED", Conflicts(mixed, 1)); // dated after the shift ended
        var row = await o.FuelingRowAsync(FuelingId(mixed, 0));
        Assert.Equal(pump.WorkerId, row["agent_worker_id"]);
        Assert.Equal(await o.AgentIdAsync(pump), row["agent_id"]!.ToString());
    }

    [Fact]
    public async Task AnUnknownTagIsRejectedAndOperationsIsToldWithoutTheUidBeingKeptAnywhere()
    {
        var pump = await F.PumpAsync();
        var uid = RandomUid();
        var capture = new Capture(Guid.NewGuid(), uid, 30_000, null, DateTimeOffset.UtcNow, await o.AgentIdAsync(pump));
        var batchId = Guid.NewGuid();

        var refused = await AnsweredAsync(await o.SyncAsync(pump, batchId, 0, [capture]));
        Assert.Equal("TAG_NOT_RECOGNISED", Rejection(refused, 0));
        Assert.Equal(JsonValueKind.Null, Item(refused, 0).GetProperty("fueling").ValueKind);
        Assert.Equal(0, await o.CountAsync("select count(*) from fuelings where client_txn_id = @t", new { t = capture.ClientTxnId }));

        // One critical alert, for Operations only, naming the device and the capture.
        var alerts = await BodyAsync(await F.GetAsync("/api/v1/ops/alerts?type=SYNC_REJECTED&limit=100", await F.OpsAsync()), 200);
        var mine = alerts.GetProperty("items").EnumerateArray().Where(a => a.GetProperty("deviceId").GetString() == pump.DeviceId).ToList();
        Assert.Single(mine);
        Assert.Equal("CRITICAL", mine[0].GetProperty("severity").GetString());
        Assert.Equal(pump.StationId, mine[0].GetProperty("stationId").GetString());
        Assert.Equal(pump.OperatorId, mine[0].GetProperty("operatorId").GetString());
        Assert.Equal(JsonValueKind.Null, mine[0].GetProperty("companyId").ValueKind);
        var details = mine[0].GetProperty("details");
        Assert.Equal("TAG_NOT_RECOGNISED", details.GetProperty("code").GetString());
        Assert.Equal(capture.ClientTxnId.ToString(), details.GetProperty("clientTxnId").GetString());
        Assert.Equal(uid[^4..], details.GetProperty("uidLast4").GetString());
        Assert.Equal(pump.DeviceCode, details.GetProperty("deviceCode").GetString());
        Assert.Equal(batchId.ToString(), details.GetProperty("batchId").GetString());
        Assert.Equal(30_000, details.GetProperty("volumeMl").GetInt64());
        Assert.False(await o.Owner.QueryOneAsync<bool>("select company_visible from alerts where device_id = @d::uuid", new { d = pump.DeviceId }));

        // The UID itself is in no alert and no audit row, in either case.
        Assert.Equal(0, await o.CountAsync("select count(*) from alerts where details::text ilike @p", new { p = "%" + uid + "%" }));
        Assert.Equal(0, await o.CountAsync("select count(*) from audit_log where metadata::text ilike @p", new { p = "%" + uid + "%" }));

        // Sent again (the answer was lost): rejected again, and Operations is not told twice.
        Assert.Equal("TAG_NOT_RECOGNISED", Rejection(await AnsweredAsync(await o.SyncAsync(pump, batchId, 0, [capture])), 0));
        Assert.Equal(1, await o.CountAsync("select count(*) from alerts where type = 'SYNC_REJECTED' and device_id = @d::uuid", new { d = pump.DeviceId }));
        var batches = await o.BatchRowsAsync(pump);
        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.Equal(1, b["rejected"]));

        // A string that is not a UID at all leaves nothing of itself behind.
        Assert.Equal("TAG_NOT_RECOGNISED", Rejection(await AnsweredAsync(await o.SyncAsync(pump,
            new Capture(Guid.NewGuid(), "not-a-uid-QQ77", 1_000, null, DateTimeOffset.UtcNow, await o.AgentIdAsync(pump)))), 0));
        Assert.Equal(0, await o.CountAsync("select count(*) from alerts where details::text ilike '%QQ77%'"));
    }

    [Fact]
    public async Task TheSameRejectedItemSentInParallelIsReportedOnce()
    {
        // Beyond Java: the rule "Operations is told once" holds when a terminal resends the same batch before the first
        // answer arrives (V8's unique index, answered by ON CONFLICT DO NOTHING).
        var pump = await F.PumpAsync();
        var capture = new Capture(Guid.NewGuid(), RandomUid(), 1_000, null, DateTimeOffset.UtcNow, await o.AgentIdAsync(pump));
        var batchId = Guid.NewGuid();

        var answers = await Concurrently.RunAsync(5, async _ => Rejection(await AnsweredAsync(await o.SyncAsync(pump, batchId, 0, [capture])), 0));

        Assert.All(answers, code => Assert.Equal("TAG_NOT_RECOGNISED", code));
        Assert.Equal(1, await o.CountAsync("select count(*) from alerts where type = 'SYNC_REJECTED' and device_id = @d::uuid", new { d = pump.DeviceId }));
        Assert.Equal(5, (await o.BatchRowsAsync(pump)).Count);
    }

    [Fact]
    public async Task OneItemThatCannotBeRecordedDoesNotHoldBackTheOthers()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 100_000);
        var good = await o.CaptureAsync(pump, await F.CarAsync(company), 10_000);
        var unknownTag = new Capture(Guid.NewGuid(), RandomUid(), 10_000, null, DateTimeOffset.UtcNow, await o.AgentIdAsync(pump));
        var tooMuch = await o.CaptureAsync(pump, await F.CarAsync(company), FuelingEngine.MaxVolumeMl + 1);
        var nothing = await o.CaptureAsync(pump, await F.CarAsync(company), 0);
        var fromTheFuture = (await o.CaptureAsync(pump, await F.CarAsync(company), 10_000)).At(DateTimeOffset.UtcNow.AddMinutes(10));
        var alsoGood = await o.CaptureAsync(pump, await F.CarAsync(company), 20_000);

        var answer = await AnsweredAsync(await o.SyncAsync(pump, good, unknownTag, tooMuch, nothing, fromTheFuture, alsoGood));
        Assert.Equal(["RECORDED", "REJECTED", "REJECTED", "REJECTED", "RECORDED", "RECORDED"],
            answer.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("result").GetString()));
        Assert.Equal("TAG_NOT_RECOGNISED", Rejection(answer, 1));
        Assert.Equal("INVALID_VOLUME", Rejection(answer, 2));
        Assert.Equal("INVALID_VOLUME", Rejection(answer, 3));
        // Dated ten minutes ahead: the fuel left the pump, so it is recorded, judged at arrival, flagged.
        Assert.Contains("DEVICE_CLOCK_SKEW", Conflicts(answer, 4));

        Assert.Equal(100_000 - 15_000 - 15_000 - 30_000, await o.WalletBalanceAsync(company));
        Assert.Equal(3, await o.CountAsync("select count(*) from fuelings where company_id = @c::uuid", new { c = company }));
        var log = (await o.BatchRowsAsync(pump))[0];
        Assert.Equal(6, log["item_count"]);
        Assert.Equal(3, log["recorded"]);
        Assert.Equal(3, log["rejected"]);
        Assert.Equal(0, log["duplicates"]);
        Assert.Equal(3, await o.CountAsync("select count(*) from alerts where type = 'SYNC_REJECTED' and device_id = @d::uuid", new { d = pump.DeviceId }));
    }

    [Fact]
    public async Task ATransactionIdBelongsToOneDeviceAndOneFueling()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 100_000);
        var car = await F.CarAsync(company);
        var capture = await o.CaptureAsync(pump, car, 10_000);
        Assert.Equal(["RECORDED"], await ResultsAsync(await o.SyncAsync(pump, capture)));

        // The same id for another volume or another tag is not that fueling.
        var changed = await AnsweredAsync(await o.SyncAsync(pump, capture.Volume(12_000),
            new Capture(capture.ClientTxnId, (await F.CarAsync(company)).TagUid, 10_000, null, capture.DeviceAt, capture.AgentId)));
        Assert.Equal("IDEMPOTENCY_MISMATCH", Rejection(changed, 0));
        Assert.Equal("IDEMPOTENCY_MISMATCH", Rejection(changed, 1));
        // The same id from another device is not this device's fueling.
        var other = await F.Pos.PumpAtAsync(pump.OperatorId, pump.StationId);
        Assert.Equal("CLIENT_TXN_CONFLICT", Rejection(await AnsweredAsync(await o.SyncAsync(other,
            new Capture(capture.ClientTxnId, car.TagUid, 10_000, null, DateTimeOffset.UtcNow, await o.AgentIdAsync(other)))), 0));
        // An online retry of a synced capture gets the stored fueling, as any replay does.
        var replay = await BodyAsync(await F.FuelAsync(pump, car, capture.ClientTxnId, 10_000, null), 200);
        Assert.True(replay.GetProperty("replayed").GetBoolean());

        Assert.Equal(1, await o.CountAsync("select count(*) from fuelings where client_txn_id = @t", new { t = capture.ClientTxnId }));
        Assert.Equal(85_000, await o.WalletBalanceAsync(company));
    }

    [Fact]
    public async Task AnOnlineAttemptThatFellBackToTheQueueIsNeverChargedTwiceAndNeverLostSilently()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 20_000);
        var car = await F.CarAsync(company);

        // The terminal sent a fueling online, got no answer, and queued it under the same id.
        // 1. The server had completed it: the queue item is a duplicate and is charged once.
        var completed = Guid.NewGuid();
        Assert.Equal("COMPLETED", await StatusAsync(await F.FuelAsync(pump, car, completed, 10_000, null)));
        var duplicate = await AnsweredAsync(await o.SyncAsync(pump,
            new Capture(completed, car.TagUid, 10_000, null, DateTimeOffset.UtcNow, await o.AgentIdAsync(pump))));
        Assert.Equal("DUPLICATE", Item(duplicate, 0).GetProperty("result").GetString());
        Assert.Equal("ONLINE", Item(duplicate, 0).GetProperty("fueling").GetProperty("origin").GetString());
        Assert.Equal(5_000, await o.WalletBalanceAsync(company));

        // 2. The server had declined it (the wallet could not cover it) and the terminal let the fuel go on its offline rules.
        //    The id's outcome stays the decline, and the dispensed fuel is recorded too, as an offline fueling of its own,
        //    flagged DECLINED_ONLINE (B5 review 1).
        var second = await F.CarAsync(company);
        var declined = Guid.NewGuid();
        Assert.Equal("INSUFFICIENT_BALANCE", await ReasonAsync(await F.FuelAsync(pump, second, declined, 10_000, null)));
        var letGo = new Capture(declined, second.TagUid, 10_000, null, DateTimeOffset.UtcNow, await o.AgentIdAsync(pump));
        var queued = await AnsweredAsync(await o.SyncAsync(pump, letGo));
        Assert.Equal("RECORDED", Item(queued, 0).GetProperty("result").GetString());
        Assert.Contains("DECLINED_ONLINE", Conflicts(queued, 0));
        Assert.Contains("INSUFFICIENT_BALANCE", Conflicts(queued, 0));
        Assert.Equal("DECLINED", await o.Owner.QueryOneAsync<string>("select status from fuelings where client_txn_id = @t", new { t = declined }));
        // Recorded under Java's derived id (UUID.nameUUIDFromBytes), so the two APIs find each other's records.
        Assert.Equal(FuelingEngine.AfterDecline(declined),
            await o.Owner.QueryOneAsync<Guid>("select client_txn_id from fuelings where id = @id::uuid", new { id = FuelingId(queued, 0) }));
        Assert.Equal(0, await o.WalletBalanceAsync(company));
        Assert.Equal(10_000, await o.OwedAsync(company)); // 15,000 charged, 5,000 from the wallet
        // Sent again: found under the derived id, nothing moves.
        Assert.Equal(["DUPLICATE"], await ResultsAsync(await o.SyncAsync(pump, letGo)));
        Assert.Equal(10_000, await o.OwedAsync(company));
    }

    [Fact]
    public async Task AnItemThatFailsOnOurSideAnswersRetryAndTheOthersAreRecorded()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 100_000);
        var broken = await F.CompanyAsync();
        var orphan = await F.CarAsync(broken);
        // A company with no wallet is a fault on our side, not a fact about the capture.
        await o.Owner.ExecuteAsync("delete from company_wallets where company_id = @c::uuid", new { c = broken });
        var answer = await o.SyncAsync(pump, await o.CaptureAsync(pump, await F.CarAsync(company), 10_000), await o.CaptureAsync(pump, orphan, 10_000),
            await o.CaptureAsync(pump, await F.CarAsync(company), 10_000));
        Assert.Equal(["RECORDED", "RETRY", "RECORDED"], await ResultsAsync(answer));
        var log = (await o.BatchRowsAsync(pump))[0];
        Assert.Equal(1, log["retried"]);
        Assert.Equal(2, log["recorded"]);
    }

    // -------------------------------------------------------------------------------------------------- who may sync

    [Fact]
    public async Task SyncIsForADeviceAloneAndABatchHoldsAtMostOneHundred()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 100_000);
        var car = await F.CarAsync(company);
        var body = BatchBody(Guid.NewGuid(), 0, [await o.CaptureAsync(pump, car, 1_000)]);

        // No credential, a user's token, a device that was revoked: none of them is a device.
        Assert.Equal(401, (int)(await F.Id.PostAsync("/api/v1/pos/sync", body)).StatusCode);
        var user = await F.Pos.PosAsync("POST", "/api/v1/pos/sync", await F.OpsAsync(), null, body);
        Assert.Equal(401, (int)user.StatusCode);
        Assert.Equal("DEVICE_UNAUTHENTICATED", await CodeAsync(user));
        // The device token alone is enough: no agent has to be signed in (the queue is sent after a shift).
        Assert.Equal(["RECORDED"], await ResultsAsync(await F.Pos.PosAsync("POST", "/api/v1/pos/sync", pump.DeviceToken, null, body)));
        // An empty batch is a device saying it holds nothing.
        Assert.Empty(await ResultsAsync(await o.SyncAsync(pump)));

        var many = new List<Capture>();
        for (var i = 0; i < OfflineSync.MaxItems + 1; i++)
        {
            many.Add(await o.CaptureAsync(pump, car, 1_000));
        }
        var tooLarge = await o.SyncAsync(pump, Guid.NewGuid(), 0, many);
        Assert.Equal(400, (int)tooLarge.StatusCode);
        var refusal = await BodyAsync(tooLarge, 400);
        Assert.Equal("BATCH_TOO_LARGE", refusal.GetProperty("code").GetString());
        Assert.Equal("items", refusal.GetProperty("field").GetString());
        Assert.Equal(100, refusal.GetProperty("params").GetProperty("max").GetInt32());
        Assert.Equal(1, await o.CountAsync("select count(*) from fuelings where company_id = @c::uuid", new { c = company })); // none of the 101
        // A malformed item is the terminal's software at fault, not a capture to reject: 400 for the request.
        var malformed = await F.Pos.PosAsync("POST", "/api/v1/pos/sync", pump.DeviceToken, null,
            $$"""{"batchId":"{{Guid.NewGuid()}}","pending":0,"items":[{"clientTxnId":"{{Guid.NewGuid()}}","volumeMl":1000}]}""");
        Assert.Equal(400, (int)malformed.StatusCode);
        Assert.Equal("VALIDATION_FAILED", await CodeAsync(malformed));
        var nullItem = await F.Pos.PosAsync("POST", "/api/v1/pos/sync", pump.DeviceToken, null,
            $$"""{"batchId":"{{Guid.NewGuid()}}","pending":0,"items":[null]}""");
        Assert.Equal(400, (int)nullItem.StatusCode);
        Assert.Equal("VALIDATION_FAILED", await CodeAsync(nullItem));

        Assert.Equal(200, (int)(await F.Id.AuthedAsync("POST", $"/api/v1/ops/devices/{pump.DeviceId}/revoke", await F.OpsAsync(),
            """{"reason":"Stolen"}""")).StatusCode);
        var revoked = await F.Pos.PosAsync("POST", "/api/v1/pos/sync", pump.DeviceToken, null,
            BatchBody(Guid.NewGuid(), 0, [await o.CaptureAsync(pump, car, 1_000)]));
        Assert.Equal(401, (int)revoked.StatusCode);
        Assert.Equal("DEVICE_UNAUTHENTICATED", await CodeAsync(revoked));
    }

    [Fact]
    public async Task EachAudienceReadsAnOfflineFuelingWithItsConflicts()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        var id = FuelingId(await AnsweredAsync(await o.SyncAsync(pump, await o.CaptureAsync(pump, await F.CarAsync(company), 10_000))), 0);

        var detail = await BodyAsync(await F.GetAsync($"/api/v1/ops/fuelings/{id}", await F.OpsAsync()), 200);
        Assert.Equal("OFFLINE", detail.GetProperty("origin").GetString());
        Assert.Equal("COMPLETED", detail.GetProperty("status").GetString());
        Assert.Equal(["INSUFFICIENT_BALANCE"], detail.GetProperty("conflicts").EnumerateArray().Select(c => c.GetString()));
        var accountant = await F.SignInAsync(Role.ACCOUNTANT, Scope.Company(Guid.Parse(company)));
        Assert.Equal(["INSUFFICIENT_BALANCE"], (await BodyAsync(await F.GetAsync($"/api/v1/company/fuelings/{id}", accountant), 200))
            .GetProperty("conflicts").EnumerateArray().Select(c => c.GetString()));
        // An online fueling has none.
        await F.CreditAsync(company, 100_000);
        var online = await BodyAsync(await F.FuelAsync(pump, await F.CarAsync(company), 1_000), 201);
        Assert.Empty((await BodyAsync(await F.GetAsync($"/api/v1/ops/fuelings/{online.GetProperty("id").GetString()}", await F.OpsAsync()), 200))
            .GetProperty("conflicts").EnumerateArray());
    }

    // ------------------------------------------------------------------------------------- held payables, the bound

    [Fact]
    public async Task AStationThatWasNotOpenHoldsThePayableAndIsFlagged()
    {
        // B5 review 7 (Java covered it through the engine's HOLD_PAYABLE set): a capture at a station that was suspended
        // while the terminal was offline is recorded, flagged STATION_NOT_ACTIVE, critical, and its payable held.
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 100_000);
        var capture = await o.CaptureAsync(pump, await F.CarAsync(company), 10_000);
        await o.Owner.ExecuteAsync("update stations set status = 'SUSPENDED' where id = @s::uuid", new { s = pump.StationId });

        var answer = await AnsweredAsync(await o.SyncAsync(pump, capture));

        Assert.Equal(["STATION_NOT_ACTIVE"], Conflicts(answer, 0));
        var row = await o.FuelingRowAsync(FuelingId(answer, 0));
        Assert.Equal(true, row["payable_held"]);
        Assert.Equal("COMPLETED", row["status"]);
        Assert.Equal(15_000, await o.StationPayableAsync(pump.StationId)); // credited in full, held from payouts
        Assert.Equal("CRITICAL", await o.Owner.QueryOneAsync<string>(
            "select severity from alerts where type = 'SYNC_CONFLICT' and fueling_id = @f::uuid", new { f = FuelingId(answer, 0) }));
    }

    [Fact]
    public async Task ADifferentPriceOnTheTerminalIsFlaggedAndTheServerPriceIsCharged()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 100_000);
        var car = await F.CarAsync(company);
        var same = (await o.CaptureAsync(pump, car, 1_000)).Json().Replace("\"receiptLocale\"", "\"unitPricePiastres\":1500,\"receiptLocale\"",
            StringComparison.Ordinal);
        var other = (await o.CaptureAsync(pump, await F.CarAsync(company), 1_000)).Json()
            .Replace("\"receiptLocale\"", "\"unitPricePiastres\":1450.0,\"receiptLocale\"", StringComparison.Ordinal);

        var answer = await AnsweredAsync(await F.Pos.PosAsync("POST", "/api/v1/pos/sync", pump.DeviceToken, null,
            $$"""{"batchId":"{{Guid.NewGuid()}}","pending":0,"items":[{{same}},{{other}}]}"""));

        Assert.Empty(Conflicts(answer, 0));
        Assert.Equal(["PRICE_MISMATCH"], Conflicts(answer, 1)); // 1450.0 is the whole number 1450
        Assert.Equal(1_500, Item(answer, 1).GetProperty("fueling").GetProperty("unitPricePiastres").GetInt64());
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    /// <summary>Syncs one capture and checks it was recorded, completed, with exactly these conflicts and one alert.</summary>
    private async Task<JsonElement> AssertRecordedWithAsync(Pump pump, Capture capture, string severity, params string[] expected)
    {
        var response = await AnsweredAsync(await o.SyncAsync(pump, capture));
        Assert.Equal(expected, Conflicts(response, 0));
        var id = FuelingId(response, 0);
        Assert.Equal("COMPLETED", (await o.FuelingRowAsync(id))["status"]);
        // One alert for the fueling, to Operations and the company.
        Assert.Equal([severity + " [\"" + string.Join("\", \"", expected) + "\"] true"], await o.Owner.QueryAsync<string>("""
            select severity || ' ' || (details -> 'conflicts')::text || ' ' || company_visible from alerts
            where fueling_id = @id::uuid and type = 'SYNC_CONFLICT'
            """, new { id }));
        return response;
    }

    private async Task<string> TagOfAsync(Car car) =>
        (await o.Owner.QueryOneAsync<Guid>("select id from rfid_tags where vehicle_id = @v::uuid", new { v = car.VehicleId })).ToString();
}

/// <summary>The pure parts of the offline ingest, against values the Java code produces.</summary>
public sealed class OfflineSyncUnitTests
{
    [Fact]
    public void TheIdAfterADeclineIsJavasNameUuid()
    {
        // UUID.nameUUIDFromBytes("offline-after-decline:0192f0c4-1d2e-7a3b-8c4d-5e6f708192a3".getBytes(UTF_8))
        Assert.Equal(Guid.Parse("c1e87a69-dca7-3bbf-aa72-1a3cf940fe22"), FuelingEngine.AfterDecline(Guid.Parse("0192f0c4-1d2e-7a3b-8c4d-5e6f708192a3")));
    }

    [Theory]
    [InlineData("04:a1:b2:c3:d4:e5:f6", "E5F6")]
    [InlineData("04 a1-b2 c3d4e5f6", "E5F6")]
    [InlineData("04A1B2C3", "B2C3")]
    [InlineData("04A1B2C", null)] // seven characters: not a UID
    [InlineData("not-a-uid-QQ77", null)]
    [InlineData(null, null)]
    public void OnlyAUidGivesItsLastFour(string? raw, string? expected) => Assert.Equal(expected, OfflineSync.UidLast4(raw));

    [Theory]
    [InlineData("20000", 20_000L, "20000")]
    [InlineData("20000.0", 20_000L, "20000.0")]
    [InlineData("2e4", 20_000L, "2E+4")]
    [InlineData("1500.25", null, "1500.25")]
    [InlineData("-0.5", null, "-0.5")]
    [InlineData("1.5e-1", null, "0.15")]
    [InlineData("0.0000001", null, "1E-7")]
    [InlineData("1e30", null, "1E+30")]
    [InlineData("9223372036854775807", long.MaxValue, "9223372036854775807")]
    [InlineData("9223372036854775808", null, "9223372036854775808")]
    [InlineData("-9223372036854775808", long.MinValue, "-9223372036854775808")]
    [InlineData("0", 0L, "0")]
    [InlineData("0.00", 0L, "0.00")]
    public void AnItemNumberIsReadAsJavasBigDecimal(string literal, long? whole, string javaText)
    {
        var number = SyncNumber.Parse(literal)!.Value;
        Assert.Equal(whole, number.Whole);
        Assert.Equal(javaText, number.ToString());
    }
}
