using System.Security.Cryptography;
using System.Text;
using Zawed.Api.Modules.Ledger;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Ledger.LedgerHttp;

namespace Zawed.Tests.Modules.Ledger;

/// <summary>
/// Wallet adjustments (V12, ADR 0021; Java <c>WalletAdjustmentTests</c>): never a shortcut. One person asks with a
/// reason, a different person approves, the ledger posts once against platform:adjustments, and the Idempotency-Key,
/// which the request must carry (ADR 0022), makes a retried request the same adjustment.
/// </summary>
public sealed class WalletAdjustmentTests(LedgerApi api) : IClassFixture<LedgerApi>, IAsyncLifetime
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
    public async Task AnAdjustmentNeedsASecondPersonAndPostsAgainstTheAdjustmentAccount()
    {
        var company = await id.SeedCompanyAsync();
        var requested = await AskAsync(ops, company, 15_000, "Goodwill for a faulty pump", Key());
        Assert.True((int)requested.StatusCode == 201, await requested.Content.ReadAsStringAsync());
        Assert.Equal("PENDING_APPROVAL", await TextAsync(requested, "status"));
        var adjustment = await TextAsync(requested, "id");
        Assert.Equal(0, await api.CachedBalanceAsync(company));
        Assert.Equal(new WalletAdjustmentAwaitingApproval(Guid.Parse(adjustment), company, 15_000),
            Assert.Single(api.Events.Of<WalletAdjustmentAwaitingApproval>(), e => e.AdjustmentId == Guid.Parse(adjustment)));

        Assert.Equal(403, (int)(await ApproveAsync(adjustment, ops)).StatusCode); // Ops has no approval permission
        var approved = await ApproveAsync(adjustment, manager);
        Assert.Equal("APPROVED", await TextAsync(approved, "status"));
        Assert.Equal(15_000, await api.CachedBalanceAsync(company));
        Assert.Equal(15_000, await api.LedgerBalanceAsync(company));
        var txn = Guid.Parse(await TextAsync(approved, "ledgerTransactionId"));
        Assert.Equal("ADJUSTMENT", await api.Owner.QueryOneAsync<string>("select source_type from ledger_transactions where id = @txn", new { txn }));
        Assert.Equal($"ADJ-{adjustment[..8]}", await api.Owner.QueryOneAsync<string>("select reference from ledger_transactions where id = @txn", new { txn }));
        var accounts = await api.Owner.QueryAsync<string>("""
            select a.code from ledger_entries e join ledger_accounts a on a.id = e.account_id
            where e.transaction_id = @txn order by e.seq
            """, new { txn });
        Assert.Equal(new[] { "platform:adjustments", $"company:{company}:wallet" }, accounts);

        // A repeat answers it as it is; nothing posts twice.
        Assert.Equal(200, (int)(await ApproveAsync(adjustment, manager)).StatusCode);
        Assert.Equal(1, await api.CountAsync("select count(*) from ledger_transactions where source_id = @id::uuid", new { id = adjustment }));
    }

    [Fact]
    public async Task TheRequesterNeverApprovesTheirOwnAdjustment()
    {
        var company = await id.SeedCompanyAsync();
        var adjustment = await TextAsync(await AskAsync(manager, company, 1_000, "Correction", Key()), "id");
        await Http.AssertErrorAsync(await ApproveAsync(adjustment, manager), 403, "SELF_APPROVAL_NOT_ALLOWED");
        Assert.Equal(0, await api.CachedBalanceAsync(company));
        // Only the requester withdraws it.
        await Http.AssertErrorAsync(await id.AuthedAsync("POST", $"/api/v1/ops/wallet-adjustments/{adjustment}/cancel", ops.AccessToken), 403, "FORBIDDEN");
        var cancelled = await id.AuthedAsync("POST", $"/api/v1/ops/wallet-adjustments/{adjustment}/cancel", manager.AccessToken);
        Assert.Equal("CANCELLED", await TextAsync(cancelled, "status"));
        var refused = await Http.AssertErrorAsync(await ApproveAsync(adjustment, await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER))),
            409, "ADJUSTMENT_NOT_PENDING");
        Assert.Equal("CANCELLED", refused.GetProperty("params").GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnIdempotencyKeyMakesARetrySafeAndRefusesADifferentBody()
    {
        var company = await id.SeedCompanyAsync();
        var key = "adj-" + Guid.NewGuid();
        var first = await AskAsync(ops, company, 2_000, "Correction", key);
        var retry = await AskAsync(ops, company, 2_000, "Correction", key);
        Assert.Equal(await TextAsync(first, "id"), await TextAsync(retry, "id"));
        await Http.AssertErrorAsync(await AskAsync(ops, company, 3_000, "Correction", key), 409, "IDEMPOTENCY_KEY_REUSED");
        Assert.Equal(1, await api.CountAsync("select count(*) from wallet_adjustments where company_id = @company", new { company }));
        // The replay published nothing.
        Assert.Single(api.Events.Of<WalletAdjustmentAwaitingApproval>(), e => e.CompanyId == company);

        // The same key raced from several connections still creates one adjustment.
        var raced = "adj-" + Guid.NewGuid();
        var statuses = await Concurrently.RunAsync(4, async _ => (int)(await AskAsync(ops, company, 4_000, "Race", raced)).StatusCode);
        Assert.All(statuses, s => Assert.Equal(201, s));
        Assert.Equal(1, await api.CountAsync("select count(*) from wallet_adjustments where company_id = @company and reason = 'Race'", new { company }));
    }

    [Fact]
    public async Task RacingApprovalsPostOnce()
    {
        var company = await id.SeedCompanyAsync();
        var adjustment = await TextAsync(await AskAsync(ops, company, 9_000, "Race approvals", Key()), "id");
        var approvers = new List<SignedIn> { manager, await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER)) };
        id.Limiter.Reset(); // two steps per MFA sign-in: stay under the per-address sign-in limit
        approvers.Add(await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN)));
        var statuses = await Concurrently.RunAsync(approvers.Count, async i => (int)(await ApproveAsync(adjustment, approvers[i])).StatusCode);
        Assert.All(statuses, s => Assert.Equal(200, s));
        Assert.Equal(9_000, await api.CachedBalanceAsync(company));
        Assert.Equal(1, await api.CountAsync("select count(*) from ledger_transactions where source_id = @id::uuid", new { id = adjustment }));
        Assert.Equal(1, await api.CountAsync("select count(*) from audit_log where action = 'WALLET_ADJUSTMENT_APPROVED' and entity_id = @id::uuid",
            new { id = adjustment }));
    }

    [Fact]
    public async Task WithoutAnIdempotencyKeyNothingIsAskedFor()
    {
        // A retry without the header created a second pending adjustment, and an approver could approve both. The key is
        // mandatory, so every retry is recognised.
        var company = await id.SeedCompanyAsync();
        foreach (var missing in new string?[] { null, "", "   " })
        {
            var refused = await Http.AssertErrorAsync(await AskAsync(ops, company, 2_000, "No key", missing), 400, "INVALID_IDEMPOTENCY_KEY");
            Assert.Equal("Idempotency-Key", refused.GetProperty("field").GetString());
        }
        await Http.AssertErrorAsync(await AskAsync(ops, company, 2_000, "No key", "short"), 400, "INVALID_IDEMPOTENCY_KEY"); // under 8 characters
        Assert.Equal(0, await api.CountAsync("select count(*) from wallet_adjustments where company_id = @company", new { company }));

        // Permission is still decided first: a caller without WALLET_CREDIT learns nothing about the key.
        var fleet = await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER, Scope.Company(company), false));
        await Http.AssertErrorAsync(await AskAsync(fleet, company, 2_000, "No key", null), 403, "FORBIDDEN");
    }

    [Fact]
    public async Task ARetriedRequestIsOneAdjustmentAndCreditsOnce()
    {
        // The same request sent again (four times at once, as a client retrying a call that timed out), then every
        // answer approved. One adjustment, one credit.
        var company = await id.SeedCompanyAsync();
        var retried = Key();
        var answers = await Concurrently.RunAsync(4, _ => AskAsync(ops, company, 7_500, "Retried", retried));
        var ids = new List<string>();
        foreach (var answer in answers)
        {
            ids.Add(await TextAsync(answer, "id"));
        }
        Assert.Single(ids.Distinct());
        foreach (var adjustment in ids)
        {
            Assert.Equal(200, (int)(await ApproveAsync(adjustment, manager)).StatusCode);
        }
        Assert.Equal(7_500, await api.CachedBalanceAsync(company));
        Assert.Equal(7_500, await api.LedgerBalanceAsync(company));
        Assert.Equal(1, await api.CountAsync("select count(*) from wallet_adjustments where company_id = @company", new { company }));
    }

    [Fact]
    public async Task TheKeyIsHashedOverTheCompanyAndTheBodyAsJavaHashedIt()
    {
        // Beyond the Java class: Java claimed the key with List.of(companyId, body), serialised by Jackson. A key a Java
        // instance stored must answer the same way here, so the stored hash is the SHA-256 of exactly that JSON.
        var company = await id.SeedCompanyAsync();
        var key = Key();
        Assert.Equal(201, (int)(await AskAsync(ops, company, 2_500, "Hash check", key)).StatusCode);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"[\"{company}\",{{\"amountPiastres\":2500,\"reason\":\"Hash check\"}}]")));
        Assert.Equal(expected, await api.Owner.QueryOneAsync<string>(
            "select request_hash from idempotency_keys where actor_id = @user and operation = @operation and key = @key",
            new { user = ops.UserId, operation = WalletAdjustments.Operation, key }));
        Assert.Equal(expected, IdempotencyKeys.Hash(new object[] { company, new WalletAdjustmentRequest(2_500, "Hash check") }));
    }

    [Fact]
    public async Task AnUnknownCompanyIsNotFoundAndLeavesNoKeyBehind()
    {
        var key = Key();
        await Http.AssertErrorAsync(await AskAsync(ops, Guid.CreateVersion7(), 1_000, "Nobody", key), 404, "NOT_FOUND");
        Assert.Equal(0, await api.CountAsync("select count(*) from idempotency_keys where key = @key", new { key }));
    }

    private static string Key() => "adj-" + Guid.NewGuid();

    private Task<HttpResponseMessage> AskAsync(SignedIn who, Guid company, long amount, string reason, string? key) =>
        http.SendAsync("POST", $"/api/v1/ops/companies/{company}/wallet/adjustments", who.AccessToken,
            $"{{\"amountPiastres\":{amount},\"reason\":\"{reason}\"}}",
            key is null ? null : new Dictionary<string, string> { ["Idempotency-Key"] = key });

    private Task<HttpResponseMessage> ApproveAsync(string adjustment, SignedIn who) =>
        id.AuthedAsync("POST", $"/api/v1/ops/wallet-adjustments/{adjustment}/approve", who.AccessToken);
}
