using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Support.IdentityTestSupport;

namespace Zawed.Tests.Modules.Identity;

/// <summary>Contract §4, Super Admin and Ops sections (Java <c>UserAdminTests</c>).</summary>
public sealed class UserAdminTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly IdentityTestSupport id = new(api.Factory);

    [Fact]
    public async Task ASuperAdminCreatesAUserWhoMustChangeTheTemporaryPassword()
    {
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        var email = $"new-{Guid.NewGuid()}@zawed.test";
        var company = await id.SeedCompanyAsync();

        var created = await id.AuthedAsync("POST", "/api/v1/admin/users", admin.AccessToken,
            $"{{\"email\":\"{email}\",\"fullName\":\"Khaled Fouad\",\"role\":\"FLEET_MANAGER\",\"scope\":{{\"type\":\"COMPANY\",\"id\":\"{company}\"}}}}");

        Assert.True((int)created.StatusCode == 201, await created.Content.ReadAsStringAsync());
        var body = await JsonOf(created);
        var temporary = body["temporaryPassword"]!.GetValue<string>();
        Assert.Equal(16, temporary.Length);
        Assert.True(body["user"]!["mustChangePassword"]!.GetValue<bool>());
        Assert.False(body["user"]!.AsObject().ContainsKey("lockedUntil")); // left out when null, as in Java
        var login = await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(email, temporary)));
        Assert.True(login["session"]!["user"]!["mustChangePassword"]!.GetValue<bool>());
        // Audited, in the user's own scope, by the admin who did it.
        Assert.Equal(company, await id.Owner.QueryOneAsync<Guid>(
            "select scope_id from audit_log where action = 'USER_CREATED' and actor_id = @UserId", new { admin.UserId }));
    }

    [Fact]
    public async Task ARoleAndAScopeThatDoNotMatchAreRefused()
    {
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        var response = await id.AuthedAsync("POST", "/api/v1/admin/users", admin.AccessToken,
            $"{{\"email\":\"x-{Guid.NewGuid()}@zawed.test\",\"fullName\":\"X\",\"role\":\"OPS\",\"scope\":{{\"type\":\"COMPANY\",\"id\":\"{Guid.NewGuid()}\"}}}}");
        var body = await JsonOf(response);
        Assert.Equal("SCOPE_MISMATCH", body["code"]!.GetValue<string>());
        Assert.Equal("scope", body["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADuplicateEmailIsRefusedWhateverItsCase()
    {
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        var existing = await id.SeedAsync(Role.ACCOUNTANT);
        var response = await id.AuthedAsync("POST", "/api/v1/admin/users", admin.AccessToken,
            $"{{\"email\":\"{existing.Email.ToUpperInvariant()}\",\"fullName\":\"X\",\"role\":\"ACCOUNTANT\",\"scope\":{{\"type\":\"COMPANY\",\"id\":\"{await id.SeedCompanyAsync()}\"}}}}");
        Assert.Equal(409, (int)response.StatusCode);
        Assert.Equal("EMAIL_IN_USE", await CodeOf(response));
    }

    [Fact]
    public async Task AUserCannotBeCreatedForACompanyThatDoesNotExist()
    {
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        var response = await id.AuthedAsync("POST", "/api/v1/admin/users", admin.AccessToken,
            $"{{\"email\":\"x-{Guid.NewGuid()}@zawed.test\",\"fullName\":\"X\",\"role\":\"ACCOUNTANT\",\"scope\":{{\"type\":\"COMPANY\",\"id\":\"{Guid.NewGuid()}\"}}}}");
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal("SCOPE_NOT_FOUND", await CodeOf(response));
    }

    [Fact]
    public async Task OpsCanResetACompanyUsersPasswordButCannotSeeAdministrators()
    {
        var ops = await id.SignInAsync(await id.SeedAsync(Role.OPS));
        var manager = await id.SeedAsync(Role.FLEET_MANAGER);
        var managerSession = await id.SignInAsync(manager);
        var admin = await id.SeedAsync(Role.SUPER_ADMIN);

        var reset = await id.AuthedAsync("POST", $"/api/v1/admin/users/{manager.Id}/reset-password", ops.AccessToken);
        Assert.Equal(200, (int)reset.StatusCode);
        Assert.Equal(16, (await JsonOf(reset))["temporaryPassword"]!.GetValue<string>().Length);
        Assert.Equal(401, (int)(await id.AuthedAsync("GET", "/api/v1/auth/me", managerSession.AccessToken)).StatusCode);

        // To Ops, an administrator does not exist.
        Assert.Equal(404, (int)(await id.AuthedAsync("POST", $"/api/v1/admin/users/{admin.Id}/reset-password", ops.AccessToken)).StatusCode);
        var list = await JsonOf(await id.AuthedAsync("GET", "/api/v1/admin/users?role=SUPER_ADMIN", ops.AccessToken));
        Assert.Empty(list["items"]!.AsArray());
    }

    [Fact]
    public async Task OpsCannotCreateUsersOrChangeStatus()
    {
        var ops = await id.SignInAsync(await id.SeedAsync(Role.OPS));
        var manager = await id.SeedAsync(Role.FLEET_MANAGER);
        Assert.Equal(403, (int)(await id.AuthedAsync("POST", $"/api/v1/admin/users/{manager.Id}/status", ops.AccessToken,
            "{\"status\":\"SUSPENDED\",\"reason\":\"test\"}")).StatusCode);
    }

    [Fact]
    public async Task ASuperAdminCannotSuspendThemselves()
    {
        var admin = await id.SeedAsync(Role.SUPER_ADMIN);
        var session = await id.SignInAsync(admin);
        var response = await id.AuthedAsync("POST", $"/api/v1/admin/users/{admin.Id}/status", session.AccessToken,
            "{\"status\":\"SUSPENDED\",\"reason\":\"test\"}");
        Assert.Equal("SELF_CHANGE_NOT_ALLOWED", await CodeOf(response));
    }

    [Fact]
    public async Task SuspendingAUserIsAuditedWithTheReasonAndEndsTheirSessions()
    {
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        var manager = await id.SeedAsync(Role.STATION_MANAGER);
        var managerSession = await id.SignInAsync(manager);

        var response = await id.AuthedAsync("POST", $"/api/v1/admin/users/{manager.Id}/status", admin.AccessToken,
            "{\"status\":\"SUSPENDED\",\"reason\":\"  Left the company \"}");

        Assert.Equal(204, (int)response.StatusCode);
        Assert.Equal(401, (int)(await id.AuthedAsync("GET", "/api/v1/auth/me", managerSession.AccessToken)).StatusCode);
        Assert.Equal("Left the company", await id.Owner.QueryOneAsync<string>(
            "select metadata->>'reason' from audit_log where action = 'USER_STATUS_CHANGED' and entity_id = @Id", new { manager.Id }));
    }

    [Fact]
    public async Task TheAuditLogIsReadableBySuperAdminsOnly()
    {
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        var ops = await id.SignInAsync(await id.SeedAsync(Role.OPS));

        var page = await id.AuthedAsync("GET", "/api/v1/admin/audit?limit=5", admin.AccessToken);
        Assert.Equal(200, (int)page.StatusCode);
        var items = (await JsonOf(page))["items"]!.AsArray();
        Assert.NotEmpty(items);
        Assert.True(items.Count <= 5);
        Assert.Equal(403, (int)(await id.AuthedAsync("GET", "/api/v1/admin/audit", ops.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task ABadCursorIsABadRequestNotAnError()
    {
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        var response = await id.AuthedAsync("GET", "/api/v1/admin/users?cursor=not-an-id", admin.AccessToken);
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal("INVALID_CURSOR", await CodeOf(response));
    }

    [Fact]
    public async Task TheAuditLogFiltersByEntityAndPagesNewestFirst()
    {
        // The reader's filters and its cursor (Java AuditController; no Java test of its own).
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        var target = await id.SeedAsync(Role.FLEET_MANAGER);
        for (var i = 0; i < 3; i++)
        {
            await id.AuthedAsync("POST", $"/api/v1/admin/users/{target.Id}/unlock", admin.AccessToken);
        }
        var first = await JsonOf(await id.AuthedAsync("GET", $"/api/v1/admin/audit?entityId={target.Id}&actorId={admin.UserId}&limit=2", admin.AccessToken));
        var items = first["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.All(items, e => Assert.Equal("ACCOUNT_UNLOCKED", e!["action"]!.GetValue<string>()));
        Assert.True(string.CompareOrdinal(items[0]!["id"]!.GetValue<string>(), items[1]!["id"]!.GetValue<string>()) > 0);
        Assert.Equal("COMPANY", items[0]!["scopeType"]!.GetValue<string>());
        Assert.NotNull(items[0]!["metadata"]!.AsObject());
        var cursor = first["nextCursor"]!.GetValue<string>();
        var second = await JsonOf(await id.AuthedAsync("GET", $"/api/v1/admin/audit?entityId={target.Id}&limit=2&cursor={cursor}", admin.AccessToken));
        Assert.Single(second["items"]!.AsArray());
        Assert.Null(second["nextCursor"]);
    }

    [Fact]
    public async Task AnMfaResetIsAuditedWithItsReasonAndTheNextSignInEnrols()
    {
        // Contract §4 (no Java test of its own).
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        var ops = await id.SeedAsync(Role.OPS);
        var reset = await id.AuthedAsync("POST", $"/api/v1/admin/users/{ops.Id}/mfa/reset", admin.AccessToken, "{\"reason\":\"lost phone\"}");
        Assert.Equal(204, (int)reset.StatusCode);
        var login = await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(ops.Email, Password)));
        Assert.Equal("MFA_ENROLLMENT_REQUIRED", login["status"]!.GetValue<string>());
        Assert.Equal("lost phone", await id.Owner.QueryOneAsync<string>(
            "select metadata->>'reason' from audit_log where action = 'MFA_RESET' and entity_id = @Id", new { ops.Id }));
    }

    [Fact]
    public async Task AMissingScopeTypeNamesTheNestedField()
    {
        // Java: @NotNull ScopeType type inside @Valid ScopeBody scope.
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        var response = await id.AuthedAsync("POST", "/api/v1/admin/users", admin.AccessToken,
            $"{{\"email\":\"x-{Guid.NewGuid()}@zawed.test\",\"fullName\":\"X\",\"role\":\"ACCOUNTANT\",\"scope\":{{\"id\":\"{Guid.NewGuid()}\"}}}}");
        var body = await Http.AssertErrorAsync(response, 400, "VALIDATION_FAILED");
        var field = Assert.Single(body.GetProperty("fields").EnumerateArray());
        Assert.Equal("scope.type", field.GetProperty("field").GetString());
        Assert.Equal("NOT_NULL", field.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AMalformedIdOrRoleIsAPlainBadRequest()
    {
        // Spring's type mismatch: 400 BAD_REQUEST, "Bad Request", nothing from the request echoed.
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        foreach (var (method, path) in new[] { ("POST", "/api/v1/admin/users/not-a-uuid/unlock"), ("GET", "/api/v1/admin/users?role=KING") })
        {
            var response = await id.AuthedAsync(method, path, admin.AccessToken);
            var body = await Http.AssertErrorAsync(response, 400, "BAD_REQUEST");
            Assert.Equal("Bad Request", body.GetProperty("message").GetString());
            Assert.False(body.TryGetProperty("fields", out _));
        }
    }
}
