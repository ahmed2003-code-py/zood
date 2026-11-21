using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Ledger.LedgerHttp;

namespace Zawed.Tests.Modules.Ledger;

/// <summary>
/// Top-up by bank transfer with maker-checker (V12, ADR 0021; Java <c>TopUpTests</c>), end to end over HTTP: a company's
/// claim is never proof, Operations verifies what actually arrived, a different person approves, and only the verified
/// amount is credited, once. Receiving accounts MAIN and CIB (<see cref="LedgerApi"/>).
/// </summary>
public sealed class TopUpTests(LedgerApi api) : IClassFixture<LedgerApi>, IAsyncLifetime
{
    private readonly IdentityTestSupport id = new(api.Factory);
    private LedgerHttp http = null!;
    private SignedIn ops = null!;
    private SignedIn manager = null!;

    public async Task InitializeAsync()
    {
        http = new LedgerHttp(id);
        ops = await id.SignInAsync(await id.SeedAsync(Role.OPS));
        manager = await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TheVerifiedAmountIsCreditedNeverTheClaimedOne()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var requested = await RequestAsync(fleet, 250_000, Reference());
        Assert.True((int)requested.StatusCode == 201, await requested.Content.ReadAsStringAsync());
        Assert.Equal("PENDING", await TextAsync(requested, "status"));
        var topUp = await TextAsync(requested, "id");
        Assert.Equal(0, await api.CachedBalanceAsync(company)); // a claim moves nothing

        // The statement shows 240,000 arrived, not the 250,000 the company typed.
        var verified = await VerifyAsync(topUp, 240_000, "{\"evidenceReference\":\"Statement 2026-11 line 4\"");
        Assert.True((int)verified.StatusCode == 200, await verified.Content.ReadAsStringAsync());
        var verifiedBody = await BodyAsync(verified);
        Assert.Equal("PENDING_APPROVAL", verifiedBody.GetProperty("status").GetString());
        Assert.Equal(240_000, verifiedBody.GetProperty("verifiedAmountPiastres").GetInt64());
        Assert.Equal(ops.UserId.ToString(), verifiedBody.GetProperty("verifiedBy").GetString());
        Assert.Equal(0, await api.CachedBalanceAsync(company)); // a verification moves nothing

        var approved = await ApproveAsync(topUp, manager);
        Assert.True((int)approved.StatusCode == 200, await approved.Content.ReadAsStringAsync());
        var approvedBody = await BodyAsync(approved);
        Assert.Equal("CONFIRMED", approvedBody.GetProperty("status").GetString());
        Assert.Equal(manager.UserId.ToString(), approvedBody.GetProperty("decidedBy").GetString());
        Assert.Equal(250_000, approvedBody.GetProperty("amountPiastres").GetInt64()); // the claim is kept
        var txn = Guid.Parse(approvedBody.GetProperty("ledgerTransactionId").GetString()!);

        Assert.Equal(240_000, await api.CachedBalanceAsync(company));
        Assert.Equal(240_000, await api.LedgerBalanceAsync(company));
        Assert.Equal(0, await api.Owner.QueryOneAsync<long>("""
            select coalesce(sum(case direction when 'DEBIT' then amount_piastres else -amount_piastres end), 0)::bigint
            from ledger_entries where transaction_id = @txn
            """, new { txn })); // balanced
        Assert.Equal(manager.UserId, await api.Owner.QueryOneAsync<Guid>("select posted_by from ledger_transactions where id = @txn", new { txn }));
        var actions = await api.Owner.QueryAsync<string>("select action from audit_log where scope_id = @company order by occurred_at, id", new { company });
        Assert.Equal(new[] { "TOPUP_REQUESTED", "TOPUP_VERIFIED", "TOPUP_CONFIRMED" },
            actions.Where(a => a is "TOPUP_REQUESTED" or "TOPUP_VERIFIED" or "TOPUP_CONFIRMED"));

        var statement = await BodyAsync(await id.AuthedAsync("GET", "/api/v1/company/wallet/statement", fleet.AccessToken));
        Assert.Equal(240_000, statement.GetProperty("items")[0].GetProperty("amountPiastres").GetInt64());
        Assert.Equal(topUp, statement.GetProperty("items")[0].GetProperty("sourceId").GetString());

        // Each step published its event once, for wave 3 to notify.
        var topUpId = Guid.Parse(topUp);
        Assert.Equal(240_000, Assert.Single(api.Events.Of<TopUpAwaitingApproval>(), e => e.TopUpId == topUpId).AmountPiastres);
        Assert.Equal(new TopUpCredited(topUpId, company, 240_000, txn), Assert.Single(api.Events.Of<TopUpCredited>(), e => e.TopUpId == topUpId));
    }

    [Fact]
    public async Task TheVerifierCannotApproveAndPlainOpsCannotApproveAtAll()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var topUp = await TextAsync(await RequestAsync(fleet, 10_000, Reference()), "id");

        // A manager who verifies may not approve their own verification.
        var verified = await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/verify", manager.AccessToken, "{\"receivedAmountPiastres\":10000}");
        Assert.True((int)verified.StatusCode == 200, await verified.Content.ReadAsStringAsync());
        await Http.AssertErrorAsync(await ApproveAsync(topUp, manager), 403, "SELF_APPROVAL_NOT_ALLOWED");
        // An Ops user has no approval permission.
        await Http.AssertErrorAsync(await ApproveAsync(topUp, ops), 403, "FORBIDDEN");
        Assert.Equal(0, await api.CachedBalanceAsync(company));

        // A second manager may.
        Assert.Equal(200, (int)(await ApproveAsync(topUp, await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER)))).StatusCode);
        Assert.Equal(10_000, await api.CachedBalanceAsync(company));
    }

    [Fact]
    public async Task TheDatabaseRefusesASingleStepConfirmationWhateverTheCode()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var topUp = Guid.Parse(await TextAsync(await RequestAsync(fleet, 10_000, Reference()), "id"));
        await VerifyAsync(topUp.ToString(), 10_000, "{");
        // Even the owner cannot mark it confirmed by its verifier: the two-person rule (V12). Java matched the statement
        // text Spring quotes in its message; the PostgreSQL refusal itself names the top-up.
        Assert.Contains("top-up", (await RawSql.RefusedAsync(() => api.Owner.ExecuteAsync("""
            update topup_requests set status = 'CONFIRMED', decided_by = verified_by, decided_at = now(),
                   ledger_transaction_id = (select id from ledger_transactions limit 1)
            where id = @topUp
            """, new { topUp }))).Message);
        // Nor rewrite what was verified.
        Assert.Contains("written once", (await RawSql.RefusedAsync(() => api.Owner.ExecuteAsync(
            "update topup_requests set verified_amount_piastres = 99 where id = @topUp", new { topUp }))).Message);
    }

    [Fact]
    public async Task ARepeatedApprovalCreditsOnce()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var topUp = await TextAsync(await RequestAsync(fleet, 7_000, Reference()), "id");
        await VerifyAsync(topUp, 7_000, "{");
        Assert.Equal(200, (int)(await ApproveAsync(topUp, manager)).StatusCode);
        var again = await ApproveAsync(topUp, manager);
        Assert.Equal(200, (int)again.StatusCode); // a retry answers the confirmed top-up
        Assert.Equal("CONFIRMED", await TextAsync(again, "status"));
        Assert.Equal(7_000, await api.CachedBalanceAsync(company));
        Assert.Equal(1, await TopUpPostingsAsync(topUp));
        Assert.Single(api.Events.Of<TopUpCredited>(), e => e.TopUpId == Guid.Parse(topUp));
    }

    [Fact]
    public async Task RacingApprovalsByDifferentManagersCreditOnce()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var topUp = await TextAsync(await RequestAsync(fleet, 40_000, Reference()), "id");
        await VerifyAsync(topUp, 40_000, "{");
        var managers = new List<SignedIn> { manager, await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER)) };
        id.Limiter.Reset(); // two steps per MFA sign-in: stay under the per-address sign-in limit
        managers.Add(await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER)));
        managers.Add(await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN)));

        var statuses = await Concurrently.RunAsync(managers.Count, async i => (int)(await ApproveAsync(topUp, managers[i])).StatusCode);
        Assert.All(statuses, s => Assert.Equal(200, s));
        Assert.Equal(40_000, await api.CachedBalanceAsync(company));
        Assert.Equal(40_000, await api.LedgerBalanceAsync(company));
        Assert.Equal(1, await TopUpPostingsAsync(topUp));
        Assert.Equal(1, await api.CountAsync("select count(*) from audit_log where action = 'TOPUP_CONFIRMED' and entity_id = @id::uuid", new { id = topUp }));
        Assert.Single(api.Events.Of<TopUpCredited>(), e => e.TopUpId == Guid.Parse(topUp));
    }

    [Fact]
    public async Task ARejectedTopUpIsNeverCreditedAndFreesItsReference()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var reference = Reference();
        var topUp = await TextAsync(await RequestAsync(fleet, 1_000, reference), "id");

        Assert.Equal(400, (int)(await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/reject", ops.AccessToken, "{\"reason\":\" \"}")).StatusCode);
        await VerifyAsync(topUp, 1_000, "{");
        // The approver disagrees with the verification.
        var rejected = await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/reject", manager.AccessToken,
            "{\"reason\":\"Amount on the statement is for another company\"}");
        Assert.Equal("REJECTED", await TextAsync(rejected, "status"));
        var refused = await Http.AssertErrorAsync(await ApproveAsync(topUp, manager), 409, "TOPUP_NOT_AWAITING_APPROVAL");
        Assert.Equal("REJECTED", refused.GetProperty("params").GetProperty("status").GetString());
        await Http.AssertErrorAsync(await id.AuthedAsync("POST", $"/api/v1/company/topups/{topUp}/cancel", fleet.AccessToken), 409, "TOPUP_NOT_PENDING");
        Assert.Equal(0, await api.CachedBalanceAsync(company));
        Assert.Equal(201, (int)(await RequestAsync(fleet, 1_000, reference)).StatusCode); // the reference is free again
        Assert.Equal(new TopUpRejected(Guid.Parse(topUp), company, true, 1_000),
            Assert.Single(api.Events.Of<TopUpRejected>(), e => e.TopUpId == Guid.Parse(topUp)));
    }

    [Fact]
    public async Task TheCompanyMayCancelOnlyBeforeVerification()
    {
        var company = await id.SeedCompanyAsync();
        var accountant = await id.SignInAsync(await id.SeedAsync(Role.ACCOUNTANT, Scope.Company(company), false));
        var first = await TextAsync(await RequestAsync(accountant, 1_000, Reference()), "id");
        var cancelled = await id.AuthedAsync("POST", $"/api/v1/company/topups/{first}/cancel", accountant.AccessToken);
        Assert.Equal("CANCELLED", await TextAsync(cancelled, "status"));
        await Http.AssertErrorAsync(await VerifyAsync(first, 1_000, "{"), 409, "TOPUP_NOT_PENDING");

        var second = await TextAsync(await RequestAsync(accountant, 2_000, Reference()), "id");
        await VerifyAsync(second, 2_000, "{");
        await Http.AssertErrorAsync(await id.AuthedAsync("POST", $"/api/v1/company/topups/{second}/cancel", accountant.AccessToken), 409, "TOPUP_NOT_PENDING");
    }

    [Fact]
    public async Task AReferenceIsUniquePerReceivingAccount()
    {
        var company = await id.SeedCompanyAsync();
        var other = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var otherFleet = await FleetAsync(other);
        var reference = "trx 100-" + Guid.NewGuid().ToString()[..6];
        var topUp = await TextAsync(await RequestAsync(fleet, 5_000, reference), "id");
        await VerifyAsync(topUp, 5_000, "{");
        await ApproveAsync(topUp, manager);

        // The same transfer, respelled, on the same account: refused, whoever asks and however.
        var respelled = " TRX100-" + reference[8..].ToUpperInvariant() + " ";
        var inUse = await Http.AssertErrorAsync(await RequestAsync(otherFleet, 5_000, respelled), 409, "BANK_REFERENCE_IN_USE");
        Assert.Equal("bankReference", inUse.GetProperty("field").GetString());
        await Http.AssertErrorAsync(await RecordAsync(other, 5_000, respelled, "MAIN"), 409, "BANK_REFERENCE_IN_USE");
        // The same reference text on another ZAWED account is another transfer.
        var elsewhere = await RecordAsync(other, 5_000, respelled, "cib");
        Assert.True((int)elsewhere.StatusCode == 201, await elsewhere.Content.ReadAsStringAsync());
        Assert.Equal("CIB", await TextAsync(elsewhere, "receivingAccount"));
        var unknown = await Http.AssertErrorAsync(await RecordAsync(other, 5_000, Reference(), "NOPE"), 400, "UNKNOWN_RECEIVING_ACCOUNT");
        Assert.Equal("receivingAccount", unknown.GetProperty("field").GetString());
        Assert.Equal(new[] { "CIB", "MAIN" }, unknown.GetProperty("params").GetProperty("accounts").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(0, await api.CachedBalanceAsync(other));
    }

    [Fact]
    public async Task RecordingTheSameTransferTwiceAnswersTheFirstRecord()
    {
        var company = await id.SeedCompanyAsync();
        var reference = Reference();
        var first = await RecordAsync(company, 30_000, reference, null);
        Assert.True((int)first.StatusCode == 201, await first.Content.ReadAsStringAsync());
        Assert.Equal("PENDING_APPROVAL", await TextAsync(first, "status"));
        Assert.Equal("OPS_RECORDED", await TextAsync(first, "origin"));
        var retry = await RecordAsync(company, 30_000, reference, null);
        Assert.Equal(200, (int)retry.StatusCode);
        Assert.Equal(await TextAsync(first, "id"), await TextAsync(retry, "id"));
        await Http.AssertErrorAsync(await RecordAsync(company, 31_000, reference, null), 409, "BANK_REFERENCE_IN_USE"); // another amount
        Assert.Equal(0, await api.CachedBalanceAsync(company)); // recorded, not approved
        // The replay published nothing: one event for one record.
        var recorded = Guid.Parse(await TextAsync(first, "id"));
        Assert.Single(api.Events.Of<TopUpAwaitingApproval>(), e => e.TopUpId == recorded);
    }

    [Fact]
    public async Task ARecordThatDiffersInDateEvidenceOrNoteIsAConflictNotAReplay()
    {
        var company = await id.SeedCompanyAsync();
        var reference = Reference();
        var date = Today.AddDays(-2);
        var first = await RecordWithAsync(company, 8_000, reference, date, "Statement line 7", "first");
        Assert.True((int)first.StatusCode == 201, await first.Content.ReadAsStringAsync());
        await Http.AssertErrorAsync(await RecordWithAsync(company, 8_000, reference, date.AddDays(-1), "Statement line 7", "first"), 409, "BANK_REFERENCE_IN_USE");
        await Http.AssertErrorAsync(await RecordWithAsync(company, 8_000, reference, date, "Statement line 8", "first"), 409, "BANK_REFERENCE_IN_USE");
        await Http.AssertErrorAsync(await RecordWithAsync(company, 8_000, reference, date, null, "first"), 409, "BANK_REFERENCE_IN_USE");
        await Http.AssertErrorAsync(await RecordWithAsync(company, 8_000, reference, date, "Statement line 7", "second"), 409, "BANK_REFERENCE_IN_USE");
        var replay = await RecordWithAsync(company, 8_000, reference, date, "Statement line 7", "first");
        Assert.Equal(200, (int)replay.StatusCode); // the same record sent again
        Assert.Equal(await TextAsync(first, "id"), await TextAsync(replay, "id"));
        Assert.Equal(1, await api.CountAsync("select count(*) from topup_requests where company_id = @company", new { company }));

        // A company's verified request carrying the reference is not an Ops record to hand back, even when every field
        // matches.
        var other = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(other);
        var claimed = Reference();
        var topUp = await TextAsync(await RequestAsync(fleet, 9_000, claimed), "id");
        var verified = await VerifyAsync(topUp, 9_000, "{");
        var claimedDate = DateOnly.Parse(await TextAsync(verified, "transferDate"), System.Globalization.CultureInfo.InvariantCulture);
        await Http.AssertErrorAsync(await RecordWithAsync(other, 9_000, claimed, claimedDate, null, null), 409, "BANK_REFERENCE_IN_USE");
    }

    [Fact]
    public async Task RetriesOfOneRecordedTransferRacedWithoutAnyKeyAreOneTopUpAndCreditOnce()
    {
        // The transfer itself is the key: its reference is unique per receiving account among live top-ups, and the
        // insert answers a conflict by reading the first record, so retries raced at once are one top-up.
        var company = await id.SeedCompanyAsync();
        var reference = Reference();
        var answers = await Concurrently.RunAsync(4, _ => RecordAsync(company, 12_000, reference, null));
        var statuses = answers.Select(a => (int)a.StatusCode).ToList();
        Assert.Single(statuses, s => s == 201);
        Assert.All(statuses, s => Assert.True(s is 201 or 200, $"status {s}"));
        var ids = new List<string>();
        foreach (var answer in answers)
        {
            ids.Add(await TextAsync(answer, "id"));
        }
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await api.CountAsync("select count(*) from topup_requests where company_id = @company", new { company }));
        foreach (var topUp in ids)
        {
            Assert.Equal(200, (int)(await ApproveAsync(topUp, manager)).StatusCode);
        }
        Assert.Equal(12_000, await api.CachedBalanceAsync(company));
        Assert.Equal(12_000, await api.LedgerBalanceAsync(company));
    }

    [Fact]
    public async Task RacingRequestsWithOneReferenceAcceptOne()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var reference = Reference();
        var statuses = await Concurrently.RunAsync(2, async i => (int)(await RequestAsync(fleet, 1_000 + i, reference)).StatusCode);
        Assert.Equal(new[] { 201, 409 }, statuses.Order());
        Assert.Equal(1, await api.CountAsync("select count(*) from topup_requests where company_id = @company", new { company }));
    }

    [Fact]
    public async Task VerificationMayCorrectTheReferenceToWhatTheStatementSays()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var typed = Reference();
        var onStatement = Reference();
        var topUp = await TextAsync(await RequestAsync(fleet, 2_000, typed), "id");
        var verified = await VerifyAsync(topUp, 2_000, $"{{\"bankReference\":\"{onStatement}\"");
        Assert.Equal(onStatement, await TextAsync(verified, "bankReference"));
        await ApproveAsync(topUp, manager);
        Assert.Equal(onStatement, await api.Owner.QueryOneAsync<string>("select reference from ledger_transactions where source_id = @id::uuid", new { id = topUp }));
        Assert.Equal(201, (int)(await RequestAsync(fleet, 2_000, typed)).StatusCode);
        await Http.AssertErrorAsync(await RequestAsync(fleet, 2_000, onStatement), 409, "BANK_REFERENCE_IN_USE");
    }

    [Fact]
    public async Task ATransferDateInTheFutureABlankReferenceAndZeroAreRefused()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var future = await Http.AssertErrorAsync(await id.AuthedAsync("POST", "/api/v1/company/topups", fleet.AccessToken,
            Body(1_000, Reference(), Today.AddDays(2))), 400, "TRANSFER_DATE_IN_FUTURE");
        Assert.Equal("transferDate", future.GetProperty("field").GetString());
        Assert.Equal(400, (int)(await id.AuthedAsync("POST", "/api/v1/company/topups", fleet.AccessToken, Body(1_000, "   ", Today))).StatusCode);
        Assert.Equal(400, (int)(await id.AuthedAsync("POST", "/api/v1/company/topups", fleet.AccessToken, Body(0, Reference(), Today))).StatusCode);
        var topUp = await TextAsync(await RequestAsync(fleet, 1_000, Reference()), "id");
        Assert.Equal(400, (int)(await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/verify", ops.AccessToken,
            "{\"receivedAmountPiastres\":0}")).StatusCode);
        Assert.Equal(0, await api.CountAsync("select count(*) from topup_requests where company_id = @company and status <> 'PENDING'", new { company }));
    }

    [Fact]
    public async Task ACompanySeesOnlyItsOwnWalletAndTopUps()
    {
        var mine = await id.SeedCompanyAsync();
        var theirs = await id.SeedCompanyAsync();
        var me = await id.SignInAsync(await id.SeedAsync(Role.ACCOUNTANT, Scope.Company(mine), false));
        var them = await FleetAsync(theirs);
        var theirRequest = await TextAsync(await RequestAsync(them, 3_000, Reference()), "id");
        var visible = (await BodyAsync(await id.AuthedAsync("GET", "/api/v1/company/topups?limit=100", me.AccessToken)))
            .GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
        Assert.DoesNotContain(theirRequest, visible);
        Assert.Equal(404, (int)(await id.AuthedAsync("POST", $"/api/v1/company/topups/{theirRequest}/cancel", me.AccessToken)).StatusCode);
        Assert.Equal(404, (int)(await id.AuthedAsync("GET", $"/api/v1/ops/companies/{Guid.NewGuid()}/wallet", ops.AccessToken)).StatusCode);
        // The company reads its own wallet only, whatever it asks.
        var wallet = await BodyAsync(await id.AuthedAsync("GET", "/api/v1/company/wallet", me.AccessToken));
        Assert.Equal(mine.ToString(), wallet.GetProperty("companyId").GetString());
    }

    [Fact]
    public async Task TheStatementPagesNewestFirstWithRunningBalances()
    {
        var company = await id.SeedCompanyAsync();
        for (var i = 1; i <= 3; i++)
        {
            await http.FundAsync(ops, company, 1_000L * i);
        }
        var first = await BodyAsync(await id.AuthedAsync("GET", $"/api/v1/ops/companies/{company}/wallet/statement?limit=2", ops.AccessToken));
        Assert.Equal(new long[] { 6_000, 3_000 }, first.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("balanceAfterPiastres").GetInt64()));
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await BodyAsync(await id.AuthedAsync("GET", $"/api/v1/ops/companies/{company}/wallet/statement?limit=2&cursor={cursor}", ops.AccessToken));
        Assert.Equal(new long[] { 1_000 }, second.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("balanceAfterPiastres").GetInt64()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        var bad = await Http.AssertErrorAsync(await id.AuthedAsync("GET", $"/api/v1/ops/companies/{company}/wallet/statement?cursor=abc", ops.AccessToken),
            400, "INVALID_CURSOR");
        Assert.Equal("cursor", bad.GetProperty("field").GetString());
    }

    [Fact]
    public async Task APastedVariantOfAReferenceIsTheSameTransfer()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var digits = new string(Guid.NewGuid().ToString().Where(char.IsAsciiDigit).Concat("123456").ToArray())[..6];
        Assert.Equal(201, (int)(await RequestAsync(fleet, 10_000, "FT" + digits)).StatusCode);
        foreach (var variant in new[] { "FT‏" + digits, "FT " + digits, "ft-" + digits, "FT" + Arabic(digits), "ＦＴ" + digits, " F.T " + digits + " " })
        {
            await Http.AssertErrorAsync(await RequestAsync(fleet, 10_000, variant), 409, "BANK_REFERENCE_IN_USE");
        }
        await Http.AssertErrorAsync(await RequestAsync(fleet, 10_000, "- ‏ ."), 400, "INVALID_BANK_REFERENCE");
    }

    [Fact]
    public async Task AReferenceWithANulCharacterIsRefusedBeforeTheDatabaseSeesIt()
    {
        // PostgreSQL text cannot hold U+0000: normalize() would fail with 22021 and the request would answer 500. The
        // reference is refused as unusable instead, on every path that takes one, and nothing is written.
        var company = await id.SeedCompanyAsync();
        var fleet = await FleetAsync(company);
        var digits = Guid.NewGuid().ToString("N")[..8];
        var requested = await Http.AssertErrorAsync(await RequestAsync(fleet, 10_000, "FT\\u0000" + digits), 400, "INVALID_BANK_REFERENCE");
        Assert.Equal("bankReference", requested.GetProperty("field").GetString());
        await Http.AssertErrorAsync(await RecordAsync(company, 10_000, digits + "\\u0000", null), 400, "INVALID_BANK_REFERENCE");
        await Http.AssertErrorAsync(await RecordAsync(company, 10_000, "\\u0000", null), 400, "INVALID_BANK_REFERENCE");

        var claim = await RequestAsync(fleet, 10_000, "FT" + digits);
        Assert.Equal(201, (int)claim.StatusCode);
        await Http.AssertErrorAsync(await VerifyAsync(await TextAsync(claim, "id"), 10_000, $"{{\"bankReference\":\"FT\\u0000{digits}\""),
            400, "INVALID_BANK_REFERENCE");
        Assert.Equal(1, await api.CountAsync("select count(*) from topup_requests where company_id = @company", new { company }));
    }

    [Fact]
    public async Task AReceivingAccountIsUpperCasedAsJavaDoes()
    {
        // Java's toUpperCase(Locale.ROOT) makes the dotless i an I, which .NET's invariant upper case does not.
        var company = await id.SeedCompanyAsync();
        var recorded = await RecordAsync(company, 5_000, Reference(), " cıb ");
        Assert.True((int)recorded.StatusCode == 201, await recorded.Content.ReadAsStringAsync());
        Assert.Equal("CIB", await TextAsync(recorded, "receivingAccount"));

        // The settings read the same way: trim(), then toUpperCase(Locale.ROOT); a label entry blank to Java is skipped.
        var settings = new Api.Modules.Ledger.FinanceSettings([" maın ", "straße"], ["cıb = CIB ••1234", " \t"]);
        Assert.Equal(new[] { "MAIN", "STRASSE" }, settings.ReceivingAccounts.Order(StringComparer.Ordinal));
        Assert.Equal("CIB ••1234", settings.LabelOf("CIB"));
        Assert.Throws<InvalidOperationException>(() => new Api.Modules.Ledger.FinanceSettings(["MAIN"], ["\u00A0"]));
    }

    [Fact]
    public async Task TheStatementCursorIsReadAsJavasLongParseLongReadsIt()
    {
        // Long.parseLong takes any Unicode decimal digit: an Arabic-Indic cursor pages as its ASCII twin.
        var company = await id.SeedCompanyAsync();
        for (var i = 1; i <= 3; i++)
        {
            await http.FundAsync(ops, company, 1_000L * i);
        }
        var first = await BodyAsync(await id.AuthedAsync("GET", $"/api/v1/ops/companies/{company}/wallet/statement?limit=2", ops.AccessToken));
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var arabic = Uri.EscapeDataString(" " + Arabic(cursor) + " ");
        var second = await BodyAsync(await id.AuthedAsync("GET", $"/api/v1/ops/companies/{company}/wallet/statement?limit=2&cursor={arabic}", ops.AccessToken));
        Assert.Equal(new long[] { 1_000 }, second.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("balanceAfterPiastres").GetInt64()));
        await Http.AssertErrorAsync(await id.AuthedAsync("GET", $"/api/v1/ops/companies/{company}/wallet/statement?cursor=%C2%B2", ops.AccessToken),
            400, "INVALID_CURSOR");
    }

    [Fact]
    public async Task TheReferenceKeyIsCanonical()
    {
        Assert.Equal("FT123", await KeyAsync(" ft-١٢٣ "));
        Assert.Equal("FT123", await KeyAsync("FT\u200F۱۲۳"));
        Assert.Equal("FT123", await KeyAsync("ＦＴ１２３"));
        Assert.Equal("FT123", await KeyAsync("FT 123"));
        Assert.Equal("", await KeyAsync("\u200F-"));
        // Values the Java BankReferences.key gives (checked with JDK 25): Java's toUpperCase folds the dotless i, NFKC the
        // long s, the Kelvin sign and the ligature; a digit outside the BMP is a digit; a composed accent is not A-Z.
        Assert.Equal("ISKFI7", await KeyAsync("\u0131\u017F\u212A\uFB01\U0001D7D5"));
        Assert.Equal("CAF", await KeyAsync("cafe\u0301"));
        Assert.Equal("NO1TM", await KeyAsync("\u2116\u2460\u2122"));
    }

    private async Task<string> KeyAsync(string reference)
    {
        await using var tx = await api.Factory.Services.GetRequiredService<ITransactions>().BeginAsync(DbScopeContext.None);
        return await BankReferences.KeyAsync(tx, reference);
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    private async Task<SignedIn> FleetAsync(Guid company) =>
        await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER, Scope.Company(company), false));

    /// <summary>Operations verifies; <paramref name="extra"/> is the start of a JSON object with any other fields.</summary>
    private Task<HttpResponseMessage> VerifyAsync(string topUp, long received, string extra) =>
        id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/verify", ops.AccessToken,
            (extra == "{" ? "{" : extra + ",") + $"\"receivedAmountPiastres\":{received}}}");

    private Task<HttpResponseMessage> ApproveAsync(string topUp, SignedIn who) =>
        id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/approve", who.AccessToken);

    private Task<HttpResponseMessage> RecordAsync(Guid company, long amount, string reference, string? account) =>
        id.AuthedAsync("POST", $"/api/v1/ops/companies/{company}/wallet/credits", ops.AccessToken,
            $"{{\"amountPiastres\":{amount},\"bankReference\":\"{reference}\",\"transferDate\":\"{Today.AddDays(-1):yyyy-MM-dd}\""
            + (account is null ? "" : $",\"receivingAccount\":\"{account}\"") + "}");

    private Task<HttpResponseMessage> RecordWithAsync(Guid company, long amount, string reference, DateOnly date, string? evidence, string? note) =>
        id.AuthedAsync("POST", $"/api/v1/ops/companies/{company}/wallet/credits", ops.AccessToken,
            $"{{\"amountPiastres\":{amount},\"bankReference\":\"{reference}\",\"transferDate\":\"{date:yyyy-MM-dd}\""
            + (evidence is null ? "" : $",\"evidenceReference\":\"{evidence}\"") + (note is null ? "" : $",\"note\":\"{note}\"") + "}");

    private Task<HttpResponseMessage> RequestAsync(SignedIn user, long amount, string reference) =>
        id.AuthedAsync("POST", "/api/v1/company/topups", user.AccessToken, Body(amount, reference, Today));

    private static string Body(long amount, string reference, DateOnly date) =>
        $"{{\"amountPiastres\":{amount},\"bankReference\":\"{reference}\",\"transferDate\":\"{date:yyyy-MM-dd}\"}}";

    private Task<long> TopUpPostingsAsync(string topUp) =>
        api.CountAsync("select count(*) from ledger_transactions where source_type = 'TOPUP' and source_id = @id::uuid", new { id = topUp });

    private static string Arabic(string digits) => new(digits.Select(c => (char)('٠' + (c - '0'))).ToArray());
}
