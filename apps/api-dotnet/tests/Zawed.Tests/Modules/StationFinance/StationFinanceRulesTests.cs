using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.StationFinance;
using Zawed.Api.Platform.Crypto;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.StationFinance.FinanceTestSupport;

namespace Zawed.Tests.Modules.StationFinance;

/// <summary>
/// The B6 rules the Java classes hold but <c>StationFinanceTests</c> does not pin, checked through the API on the real
/// database: how a bank account number is read, encrypted and never shown; change control; the value date; the statement
/// and its summary; what each portal may read; and which fuelings can be reversed.
/// </summary>
public sealed class StationFinanceRulesTests(StationFinanceApi api) : IClassFixture<StationFinanceApi>, IAsyncLifetime
{
    private readonly FinanceTestSupport f = new(api.Factory);
    private SignedIn ops = null!;
    private SignedIn checker = null!;

    public async Task InitializeAsync()
    {
        ops = await f.OpsAsync();
        checker = await f.OpsAsync(Api.Platform.Security.Role.OPS_MANAGER);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> StationAsync() => (await f.Fueling.PumpAsync()).StationId;

    private Task<HttpResponseMessage> ChangeAsync(string station, string account) =>
        f.AuthedAsync("POST", $"/api/v1/ops/stations/{station}/bank-account/changes", ops, BankChangeBody(account));

    // ----------------------------------------------------------------------------------------------- bank accounts

    [Theory]
    [InlineData("EG38 0019 0005 0000 0000 2631 80003")] // checksum
    [InlineData("EG38 0019 0005 0000 0000 2631 8000")] // an Egyptian IBAN is 29 characters
    [InlineData("GB00")] // too short for an IBAN
    [InlineData("12345")] // too short for an account number
    [InlineData("1234567890123456789012345678901234567")] // too long
    [InlineData("12AB5678")] // neither
    [InlineData("---")]
    public async Task AnAccountNumberThatIsNeitherAnIbanNorDigitsIsRefusedWithoutRepeatingIt(string account)
    {
        var station = await StationAsync();
        var error = await Http.AssertErrorAsync(await ChangeAsync(station, account), 400, "INVALID_BANK_ACCOUNT");
        Assert.Equal("accountNumber", error.GetProperty("field").GetString());
        Assert.DoesNotContain(account.Replace(" ", "", StringComparison.Ordinal)[..3], error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gb82 west 1234 5698 7654 32", "IBAN", "GB82WEST12345698765432", "5432")]
    [InlineData("٠١٢٣٤٥٦٧٨٩", "ACCOUNT_NUMBER", "0123456789", "6789")] // Arabic-Indic digits fold to 0-9
    [InlineData("12-34-56‏78", "ACCOUNT_NUMBER", "12345678", "5678")] // dashes and a direction mark vanish
    [InlineData("1234\u00005678", "ACCOUNT_NUMBER", "12345678", "5678")] // Java folds a NUL out like any mark
    public async Task AnAccountIsReadAsJavaReadsItAndStoredEncryptedAndBoundToItsRow(string typed, string kind, string normalized, string last4)
    {
        var station = await StationAsync();
        var body = await BodyAsync(await ChangeAsync(station, typed), 201);
        Assert.Equal(kind, body.GetProperty("accountKind").GetString());
        Assert.Equal(last4, body.GetProperty("accountLast4").GetString());
        Assert.DoesNotContain(normalized, body.GetRawText(), StringComparison.Ordinal);
        var id = Guid.Parse(body.GetProperty("id").GetString()!);

        var encrypted = await f.Owner.QueryOneAsync<byte[]>("select account_encrypted from station_bank_accounts where id = @id", new { id });
        var encryptor = api.Factory.Services.GetRequiredService<DataEncryptor>();
        Assert.Equal(normalized, Encoding.UTF8.GetString(encryptor.Decrypt(encrypted!, BankAccounts.Binding(id))));
        Assert.Equal(last4, await f.Owner.QueryOneAsync<string>("select account_last4 from station_bank_accounts where id = @id", new { id }));
        // Copied into another row, it does not decrypt.
        Assert.ThrowsAny<Exception>(() => encryptor.Decrypt(encrypted!, BankAccounts.Binding(Guid.CreateVersion7())));
        // The audit trail carries the last four characters only.
        var audit = await f.Owner.QueryOneAsync<string>(
            "select metadata::text from audit_log where action = 'BANK_ACCOUNT_CHANGE_REQUESTED' and entity_id = @id", new { id });
        Assert.Contains($"\"accountLast4\": \"{last4}\"", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(normalized, audit, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBindingIsJavasBytesAndTheColumnIsRegisteredForRotationWithIt()
    {
        // ByteBuffer.allocate(20).put("sbac").putLong(msb).putLong(lsb), for 00112233-4455-6677-8899-aabbccddeeff.
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Assert.Equal(Convert.FromHexString("73626163" + "00112233445566778899aabbccddeeff"), BankAccounts.Binding(id));

        var column = Assert.Single(api.Factory.Services.GetServices<EncryptedColumn>(), c => c.Name == "station_bank_accounts.account_encrypted");
        var other = Guid.CreateVersion7();
        Assert.Equal(BankAccounts.Binding(other), column.Binding(other));
    }

    [Fact]
    public async Task OneChangeWaitsAtATimeAndARaceOfRequestsKeepsOne()
    {
        var station = await StationAsync();
        var answers = await Concurrently.RunAsync(6, i => ChangeAsync(station, "12345678" + i));
        Assert.Single(answers, a => (int)a.StatusCode == 201);
        foreach (var refused in answers.Where(a => (int)a.StatusCode != 201))
        {
            await Http.AssertErrorAsync(refused, 409, "BANK_ACCOUNT_CHANGE_PENDING");
        }
        var waiting = (await BodyAsync(answers.Single(a => (int)a.StatusCode == 201), 201)).GetProperty("id").GetString();
        var details = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/bank-account", ops), 200);
        Assert.Equal(JsonValueKind.Null, details.GetProperty("active").ValueKind);
        Assert.Equal(waiting, details.GetProperty("pending").GetProperty("id").GetString());

        // The requester may reject their own request; a decided change cannot be decided again.
        var rejected = await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/bank-account-changes/{waiting}/reject", ops,
            """{"reason":"Wrong letter"}"""), 200);
        Assert.Equal("REJECTED", rejected.GetProperty("status").GetString());
        var again = await Http.AssertErrorAsync(await f.AuthedAsync("POST", $"/api/v1/ops/bank-account-changes/{waiting}/approve", checker),
            409, "BANK_ACCOUNT_CHANGE_NOT_PENDING");
        Assert.Equal("REJECTED", again.GetProperty("params").GetProperty("status").GetString());
        await Http.AssertErrorAsync(await f.AuthedAsync("POST", $"/api/v1/ops/bank-account-changes/{Guid.CreateVersion7()}/approve", checker),
            404, "NOT_FOUND");
    }

    [Fact]
    public async Task AnApprovedChangeSupersedesTheVerifiedAccountAndSaysWhich()
    {
        var station = await StationAsync();
        var first = await f.RequestBankChangeAsync(ops, station, "111111111");
        await f.ApproveBankChangeAsync(checker, first);
        var second = await f.RequestBankChangeAsync(ops, station, "222222222");
        await f.ApproveBankChangeAsync(checker, second);

        var changes = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/bank-account-changes?stationId={station}", ops), 200);
        Assert.Equal([("ACTIVE", "2222"), ("SUPERSEDED", "1111")], changes.GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("status").GetString(), i.GetProperty("accountLast4").GetString())));
        Assert.NotEqual(JsonValueKind.Null, changes.GetProperty("items")[1].GetProperty("supersededAt").ValueKind);
        var audit = await f.Owner.QueryOneAsync<string>(
            "select metadata->>'replacedAccountId' from audit_log where action = 'BANK_ACCOUNT_CHANGE_APPROVED' and entity_id = @id::uuid",
            new { id = second });
        Assert.Equal(first, audit);
        var superseded = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/bank-account-changes?stationId={station}&status=SUPERSEDED", ops), 200);
        Assert.Equal([first], superseded.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task AnUnknownStationOrAnotherOperatorsStationIsNotFound()
    {
        await Http.AssertErrorAsync(await ChangeAsync(Guid.CreateVersion7().ToString(), "12345678"), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{Guid.CreateVersion7()}/payable", ops), 404, "NOT_FOUND");
        var pump = await f.Fueling.PumpAsync();
        var stranger = await f.ManagerAsync(await f.Fueling.Pos.OperatorAsync());
        foreach (var path in new[] { "payable", "statement", "statement/summary", "bank-account" })
        {
            await Http.AssertErrorAsync(await f.AuthedAsync("GET", $"/api/v1/station/stations/{pump.StationId}/{path}", stranger), 404, "NOT_FOUND");
        }
    }

    // ------------------------------------------------------------------------------------------------------ payouts

    [Fact]
    public async Task AValueDateMayBeAtMostSevenDaysAheadAndTheBodyIsValidated()
    {
        var (pump, _, car) = await f.StationWithCustomerAsync();
        await f.FuelAsync(pump, car, 20_000);
        await f.ApproveBankChangeAsync(checker, await f.RequestBankChangeAsync(ops, pump.StationId));

        var tooFar = await Http.AssertErrorAsync(await f.PayoutAsync(ops, pump.StationId, 1_000, Ref(), LedgerHttp.Today.AddDays(8)),
            400, "VALUE_DATE_TOO_FAR");
        Assert.Equal("valueDate", tooFar.GetProperty("field").GetString());
        Assert.Equal(7, tooFar.GetProperty("params").GetProperty("maxDaysAhead").GetInt32());
        Assert.Equal(201, (int)(await f.PayoutAsync(ops, pump.StationId, 1_000, Ref(), LedgerHttp.Today.AddDays(7))).StatusCode);

        var invalid = await Http.AssertErrorAsync(await f.AuthedAsync("POST", $"/api/v1/ops/stations/{pump.StationId}/payouts", ops,
            """{"amountPiastres":0,"bankReference":" "}"""), 400, "VALIDATION_FAILED");
        var fields = invalid.GetProperty("fields").EnumerateArray().Select(x => x.GetProperty("field").GetString()).Order().ToList();
        Assert.Equal(["amountPiastres", "bankReference", "valueDate"], fields);
        await Http.AssertErrorAsync(await f.PayoutAsync(ops, pump.StationId, 1_000, "--"), 400, "INVALID_BANK_REFERENCE");
        Assert.Equal(29_000, await f.StationPayableAsync(pump.StationId));
    }

    [Fact]
    public async Task APayoutAtOnceIsAuditedWithTheAccountsLastDigitsAndTheStationReadsOnlyPostedPayouts()
    {
        var (pump, _, car) = await f.StationWithCustomerAsync(tankMl: 100_000);
        await f.FuelAsync(pump, car, 80_000); // 120,000
        await f.ApproveBankChangeAsync(checker, await f.RequestBankChangeAsync(ops, pump.StationId));
        var now = await BodyAsync(await f.PayoutAsync(ops, pump.StationId, 2_000, Ref()), 201);
        Assert.False(now.GetProperty("approvalRequired").GetBoolean());
        Assert.Equal(StationFinanceApi.Threshold, now.GetProperty("approvalThresholdPiastres").GetInt64());
        Assert.Equal(JsonValueKind.Null, now.GetProperty("decidedBy").ValueKind);
        var waiting = await BodyAsync(await f.PayoutAsync(ops, pump.StationId, 101_000, Ref()), 201);

        var audit = JsonDocument.Parse((await f.Owner.QueryOneAsync<string>(
            "select metadata::text from audit_log where action = 'PAYOUT_POSTED' and entity_id = @id::uuid",
            new { id = now.GetProperty("id").GetString() }))!).RootElement;
        Assert.Equal("0002", audit.GetProperty("accountLast4").GetString());
        Assert.Equal(118_000, audit.GetProperty("payableAfterPiastres").GetInt64());
        Assert.False(audit.GetProperty("approvalRequired").GetBoolean());
        Assert.Equal(1, await f.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where action = 'PAYOUT_RECORDED' and entity_id = @id::uuid", new { id = waiting.GetProperty("id").GetString() }));

        var ops2 = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/payouts?stationId={pump.StationId}&status=PENDING", ops), 200);
        Assert.Equal([waiting.GetProperty("id").GetString()], ops2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        var manager = await f.ManagerAsync(pump.OperatorId);
        var mine = await BodyAsync(await f.AuthedAsync("GET", "/api/v1/station/payouts", manager), 200);
        Assert.Equal([now.GetProperty("id").GetString()], mine.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        // Row-level security hides the waiting payout from the operator: its position has no pending figure to subtract.
        var position = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/station/stations/{pump.StationId}/payable", manager), 200);
        Assert.Equal(118_000, position.GetProperty("availablePiastres").GetInt64());
        var opsPosition = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{pump.StationId}/payable", ops), 200);
        Assert.Equal(101_000, opsPosition.GetProperty("pendingPayoutsPiastres").GetInt64());
    }

    // ---------------------------------------------------------------------------------------------------- statement

    [Fact]
    public async Task TheStatementPagesBySequenceAndTheSummaryAddsUp()
    {
        var empty = await StationAsync();
        var nothing = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{empty}/statement", ops), 200);
        Assert.Empty(nothing.GetProperty("items").EnumerateArray());
        var zero = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{empty}/statement/summary", ops), 200);
        Assert.Equal(0, zero.GetProperty("entries").GetInt32());
        Assert.Equal(0, zero.GetProperty("now").GetProperty("balancePiastres").GetInt64());

        var (pump, company, car) = await f.StationWithCustomerAsync();
        var station = pump.StationId;
        await f.FuelAsync(pump, car, 20_000); // +30,000
        var start = DateTimeOffset.UtcNow;
        await Task.Delay(20);
        await f.FuelAsync(pump, await f.Fueling.CarAsync(company), 10_000); // +15,000
        await f.ApproveBankChangeAsync(checker, await f.RequestBankChangeAsync(ops, station));
        await BodyAsync(await f.PayoutAsync(ops, station, 5_000, Ref()), 201); // -5,000

        var first = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/statement?limit=2", ops), 200);
        Assert.Equal([40_000L, 45_000], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("balanceAfterPiastres").GetInt64()));
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/statement?limit=2&cursor={cursor}", ops), 200);
        var last = Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.Equal(30_000, last.GetProperty("balanceAfterPiastres").GetInt64());
        Assert.False(last.GetProperty("fueling").GetProperty("payableHeld").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        await Http.AssertErrorAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/statement?cursor=abc", ops), 400, "INVALID_CURSOR");

        var all = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/statement/summary", ops), 200);
        Assert.Equal((0L, 45_000L, 5_000L, 40_000L, 3), (all.GetProperty("openingPiastres").GetInt64(), all.GetProperty("creditsPiastres").GetInt64(),
            all.GetProperty("debitsPiastres").GetInt64(), all.GetProperty("closingPiastres").GetInt64(), all.GetProperty("entries").GetInt32()));
        Assert.Equal(40_000, all.GetProperty("now").GetProperty("availablePiastres").GetInt64());
        var from = Uri.EscapeDataString(start.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture));
        var since = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/stations/{station}/statement/summary?from={from}", ops), 200);
        Assert.Equal((30_000L, 15_000L, 5_000L, 40_000L, 2), (since.GetProperty("openingPiastres").GetInt64(), since.GetProperty("creditsPiastres").GetInt64(),
            since.GetProperty("debitsPiastres").GetInt64(), since.GetProperty("closingPiastres").GetInt64(), since.GetProperty("entries").GetInt32()));
        var period = await Http.AssertErrorAsync(await f.AuthedAsync("GET",
            $"/api/v1/ops/stations/{station}/statement/summary?from={from}&to={from}", ops), 400, "INVALID_PERIOD");
        Assert.Equal("from", period.GetProperty("field").GetString());

        var manager = await f.ManagerAsync(pump.OperatorId);
        var portal = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/station/stations/{station}/statement/summary", manager), 200);
        Assert.Equal(40_000, portal.GetProperty("closingPiastres").GetInt64());
        Assert.Equal(JsonValueKind.Null, portal.GetProperty("now").GetProperty("pendingPayoutsPiastres").ValueKind);
    }

    // ---------------------------------------------------------------------------------------------------- reversals

    [Fact]
    public async Task OnlyAChargedCompletedFuelingCanBeReversedAndEachSideReadsWhatItMay()
    {
        await Http.AssertErrorAsync(await f.RequestReversalAsync(ops, Guid.CreateVersion7().ToString()), 404, "NOT_FOUND");
        var (pump, company, car) = await f.StationWithCustomerAsync(credit: 1_000);
        var declined = await BodyAsync(await f.Fueling.FuelAsync(pump, car, 20_000), 201); // the wallet cannot pay 30,000
        Assert.Equal("DECLINED", declined.GetProperty("status").GetString());
        await Http.AssertErrorAsync(await f.RequestReversalAsync(ops, declined.GetProperty("id").GetString()!), 409, "FUELING_NOT_REVERSIBLE");

        var fueling = await f.FuelAsync(pump, await f.Fueling.CarAsync(company), 500); // 750
        var requested = await BodyAsync(await f.RequestReversalAsync(ops, fueling, "  Pump test  "), 201);
        Assert.Equal("Pump test", requested.GetProperty("reason").GetString());
        Assert.Equal(750, requested.GetProperty("amountPiastres").GetInt64());
        var id = requested.GetProperty("id").GetString();
        var manager = await f.ManagerAsync(pump.OperatorId);
        var fleet = await f.FleetManagerAsync(company);
        Assert.Empty((await BodyAsync(await f.AuthedAsync("GET", "/api/v1/station/reversals", manager), 200)).GetProperty("items").EnumerateArray());
        Assert.Empty((await BodyAsync(await f.AuthedAsync("GET", "/api/v1/company/reversals", fleet), 200)).GetProperty("items").EnumerateArray());

        var rejected = await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/reversals/{id}/reject", ops, """{"reason":"No"}"""), 200);
        Assert.Equal("REJECTED", rejected.GetProperty("status").GetString());
        await Http.AssertErrorAsync(await f.AuthedAsync("POST", $"/api/v1/ops/reversals/{id}/approve", checker), 409, "REVERSAL_NOT_PENDING");
        Assert.Equal(1_000 - 750, await f.WalletBalanceAsync(company));

        // A rejected request does not block a new one, which a second person approves.
        var again = (await BodyAsync(await f.RequestReversalAsync(ops, fueling), 201)).GetProperty("id").GetString();
        var approved = await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/reversals/{again}/approve", checker), 200);
        Assert.Equal(checker.UserId.ToString(), approved.GetProperty("decidedBy").GetString());
        Assert.Equal(1_000, await f.WalletBalanceAsync(company));

        var forStation = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/station/reversals?stationId={pump.StationId}", manager), 200);
        var item = Assert.Single(forStation.GetProperty("items").EnumerateArray());
        Assert.Equal(again, item.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("decidedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("requestedBy").ValueKind);
        var otherCompany = await f.FleetManagerAsync(await f.Fueling.CompanyAsync());
        Assert.Empty((await BodyAsync(await f.AuthedAsync("GET", "/api/v1/company/reversals", otherCompany), 200)).GetProperty("items").EnumerateArray());
        var opsList = await BodyAsync(await f.AuthedAsync("GET", $"/api/v1/ops/reversals?fuelingId={fueling}", ops), 200);
        Assert.Equal(["APPROVED", "REJECTED"], opsList.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()));
        Assert.Equal(ops.UserId.ToString(), opsList.GetProperty("items")[0].GetProperty("requestedBy").GetString());
        var audit = JsonDocument.Parse((await f.Owner.QueryOneAsync<string>(
            "select metadata::text from audit_log where action = 'REVERSAL_APPROVED' and entity_id = @id::uuid", new { id = again }))!).RootElement;
        Assert.Equal(750, audit.GetProperty("walletCreditedPiastres").GetInt64());
        Assert.Equal(1_000, audit.GetProperty("walletAfterPiastres").GetInt64());
        Assert.Equal(0, audit.GetProperty("payableAfterPiastres").GetInt64());
    }
}
