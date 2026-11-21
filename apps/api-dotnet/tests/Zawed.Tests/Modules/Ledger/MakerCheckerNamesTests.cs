using System.Text.Json;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Ledger.LedgerHttp;

namespace Zawed.Tests.Modules.Ledger;

/// <summary>
/// Who asked, verified and decided, by name (QA round 1: the cards showed "Verified by 01a1f8dd"; Java
/// <c>MakerCheckerNamesTests</c>). Operations reads every name and role. A company reads its own users' names; a
/// platform actor is only <c>ZAWED_OPERATIONS</c>, never a staff member's name or e-mail. The receiving account is shown
/// by its label when one is configured (MAIN is labelled, CIB is not: <see cref="LedgerApi"/>).
/// </summary>
public sealed class MakerCheckerNamesTests(LedgerApi api) : IClassFixture<LedgerApi>, IAsyncLifetime
{
    private readonly IdentityTestSupport id = new(api.Factory);
    private LedgerHttp http = null!;
    private SeededUser opsUser = null!;
    private SignedIn ops = null!;
    private SignedIn manager = null!;

    private static string TodayText => Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async Task InitializeAsync()
    {
        http = new LedgerHttp(id);
        opsUser = await id.SeedAsync(Role.OPS);
        await RenameAsync(opsUser.Id, "Hana Operations");
        ops = await id.SignInAsync(opsUser);
        var managerUser = await id.SeedAsync(Role.OPS_MANAGER);
        await RenameAsync(managerUser.Id, "Karim Manager");
        manager = await id.SignInAsync(managerUser);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ATopUpCarriesNamesForOperationsAndOnlyTheCompanysOwnNamesForTheCompany()
    {
        var company = await id.SeedCompanyAsync();
        var fleetUser = await id.SeedAsync(Role.FLEET_MANAGER, Scope.Company(company), false);
        await RenameAsync(fleetUser.Id, "Mona Fleet");
        var fleet = await id.SignInAsync(fleetUser);
        var accountant = await id.SignInAsync(await id.SeedAsync(Role.ACCOUNTANT, Scope.Company(company), false));

        var requested = await id.AuthedAsync("POST", "/api/v1/company/topups", fleet.AccessToken,
            $"{{\"amountPiastres\":50000,\"bankReference\":\"FT-{Guid.NewGuid()}\",\"transferDate\":\"{TodayText}\"}}");
        Assert.True((int)requested.StatusCode == 201, await requested.Content.ReadAsStringAsync());
        Assert.Equal("Mona Fleet", await TextAsync(requested, "requestedByName"));
        Assert.Equal("FLEET_MANAGER", await TextAsync(requested, "requestedByRole"));
        var topUp = await TextAsync(requested, "id");

        var verified = await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/verify", ops.AccessToken,
            "{\"receivedAmountPiastres\":50000,\"receivingAccount\":\"MAIN\"}");
        Assert.True((int)verified.StatusCode == 200, await verified.Content.ReadAsStringAsync());
        var approved = await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/approve", manager.AccessToken);
        Assert.True((int)approved.StatusCode == 200, await approved.Content.ReadAsStringAsync());

        // Operations: every name and role, and the account's label.
        var forOps = await BodyAsync(await id.AuthedAsync("GET", $"/api/v1/ops/topups/{topUp}", ops.AccessToken));
        Assert.Equal("Mona Fleet", forOps.GetProperty("requestedByName").GetString());
        Assert.Equal("FLEET_MANAGER", forOps.GetProperty("requestedByRole").GetString());
        Assert.Equal("Hana Operations", forOps.GetProperty("verifiedByName").GetString());
        Assert.Equal("OPS", forOps.GetProperty("verifiedByRole").GetString());
        Assert.Equal("Karim Manager", forOps.GetProperty("decidedByName").GetString());
        Assert.Equal("OPS_MANAGER", forOps.GetProperty("decidedByRole").GetString());
        Assert.Equal("MAIN", forOps.GetProperty("receivingAccount").GetString());
        Assert.Equal(LedgerApi.MainLabel, forOps.GetProperty("receivingAccountLabel").GetString());
        var listed = await BodyAsync(await id.AuthedAsync("GET", $"/api/v1/ops/topups?companyId={company}", ops.AccessToken));
        Assert.Equal(new[] { "Hana Operations" }, Items(listed, "verifiedByName"));

        // The company, through another of its users: its own colleague by name, ZAWED as a team.
        var response = await id.AuthedAsync("GET", "/api/v1/company/topups", accountant.AccessToken);
        Assert.True((int)response.StatusCode == 200, await response.Content.ReadAsStringAsync());
        var text = await response.Content.ReadAsStringAsync();
        var forCompany = JsonDocument.Parse(text).RootElement;
        Assert.Equal(new[] { "Mona Fleet" }, Items(forCompany, "requestedByName"));
        Assert.Equal(new[] { "ZAWED_OPERATIONS" }, Items(forCompany, "verifiedByRole"));
        Assert.Equal(new[] { "ZAWED_OPERATIONS" }, Items(forCompany, "decidedByRole"));
        Assert.Equal(new string?[] { null }, Items(forCompany, "verifiedByName"));
        Assert.Equal(new string?[] { null }, Items(forCompany, "decidedByName"));
        foreach (var leak in new[] { "Hana", "Karim", opsUser.Email, "\"OPS\"", "OPS_MANAGER", ops.UserId.ToString(), manager.UserId.ToString() })
        {
            Assert.DoesNotContain(leak, text); // no staff name, e-mail or id reaches a tenant
        }
        Assert.Equal(new string?[] { null }, Items(forCompany, "verifiedBy"));
        Assert.Equal(new string?[] { null }, Items(forCompany, "decidedBy"));
        Assert.Equal(new[] { fleetUser.Id.ToString() }, Items(forCompany, "requestedBy"));
        // Operations keeps every id (its own screens compare them with the signed-in user).
        Assert.Equal(ops.UserId.ToString(), forOps.GetProperty("verifiedBy").GetString());
        Assert.Equal(manager.UserId.ToString(), forOps.GetProperty("decidedBy").GetString());
    }

    [Fact]
    public async Task ACompanyThatCancelsSeesItsOwnUserAsTheDecider()
    {
        var company = await id.SeedCompanyAsync();
        var accountantUser = await id.SeedAsync(Role.ACCOUNTANT, Scope.Company(company), false);
        await RenameAsync(accountantUser.Id, "Sara Accounts");
        var accountant = await id.SignInAsync(accountantUser);
        var topUp = await TextAsync(await id.AuthedAsync("POST", "/api/v1/company/topups", accountant.AccessToken,
            $"{{\"amountPiastres\":1000,\"bankReference\":\"FT-{Guid.NewGuid()}\",\"transferDate\":\"{TodayText}\"}}"), "id");

        var cancelled = await id.AuthedAsync("POST", $"/api/v1/company/topups/{topUp}/cancel", accountant.AccessToken);
        Assert.True((int)cancelled.StatusCode == 200, await cancelled.Content.ReadAsStringAsync());
        var body = await BodyAsync(cancelled);
        Assert.Equal("Sara Accounts", body.GetProperty("decidedByName").GetString());
        Assert.Equal("ACCOUNTANT", body.GetProperty("decidedByRole").GetString());
        Assert.Equal(accountantUser.Id.ToString(), body.GetProperty("decidedBy").GetString()); // its own user's id
        Assert.Equal(JsonValueKind.Null, body.GetProperty("verifiedByRole").ValueKind); // nobody verified it
        Assert.Equal(LedgerApi.MainLabel, body.GetProperty("receivingAccountLabel").GetString());
    }

    [Fact]
    public async Task ATransferRecordedByOperationsHasNoRequesterAndAnUnlabelledAccountKeepsItsCode()
    {
        var company = await id.SeedCompanyAsync();
        var recorded = await id.AuthedAsync("POST", $"/api/v1/ops/companies/{company}/wallet/credits", ops.AccessToken,
            $"{{\"amountPiastres\":7000,\"receivingAccount\":\"CIB\",\"bankReference\":\"FT-{Guid.NewGuid()}\",\"transferDate\":\"{TodayText}\"}}");
        Assert.True((int)recorded.StatusCode == 201, await recorded.Content.ReadAsStringAsync());
        var body = await BodyAsync(recorded);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("requestedByName").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("requestedByRole").ValueKind);
        Assert.Equal("Hana Operations", body.GetProperty("verifiedByName").GetString());
        Assert.Equal("CIB", body.GetProperty("receivingAccount").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("receivingAccountLabel").ValueKind);
    }

    [Fact]
    public async Task AWalletAdjustmentNamesWhoAskedAndWhoDecided()
    {
        var company = await id.SeedCompanyAsync();
        var asked = await http.SendAsync("POST", $"/api/v1/ops/companies/{company}/wallet/adjustments", ops.AccessToken,
            "{\"amountPiastres\":3000,\"reason\":\"Goodwill\"}", new Dictionary<string, string> { ["Idempotency-Key"] = "names-" + Guid.NewGuid() });
        Assert.True((int)asked.StatusCode == 201, await asked.Content.ReadAsStringAsync());
        var askedBody = await BodyAsync(asked);
        Assert.Equal("Hana Operations", askedBody.GetProperty("requestedByName").GetString());
        Assert.Equal("OPS", askedBody.GetProperty("requestedByRole").GetString());
        Assert.Equal(JsonValueKind.Null, askedBody.GetProperty("decidedByName").ValueKind);
        var adjustment = askedBody.GetProperty("id").GetString();

        var rejected = await id.AuthedAsync("POST", $"/api/v1/ops/wallet-adjustments/{adjustment}/reject", manager.AccessToken, "{\"reason\":\"No ticket\"}");
        Assert.True((int)rejected.StatusCode == 200, await rejected.Content.ReadAsStringAsync());
        Assert.Equal("Karim Manager", await TextAsync(rejected, "decidedByName"));
        Assert.Equal("OPS_MANAGER", await TextAsync(rejected, "decidedByRole"));
        var listed = await BodyAsync(await id.AuthedAsync("GET", $"/api/v1/ops/wallet-adjustments?companyId={company}", ops.AccessToken));
        Assert.Equal(new[] { "Hana Operations" }, Items(listed, "requestedByName"));
    }

    private Task RenameAsync(Guid user, string name) =>
        api.Owner.ExecuteAsync("update users set full_name = @name where id = @user", new { name, user });

    private static string?[] Items(JsonElement page, string property) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty(property).GetString()).ToArray();
}
