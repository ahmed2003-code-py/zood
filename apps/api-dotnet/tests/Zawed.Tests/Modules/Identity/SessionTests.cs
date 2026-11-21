using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Support.IdentityTestSupport;

namespace Zawed.Tests.Modules.Identity;

/// <summary>Contract §3: refresh rotation, race grace, reuse detection, logout, revocation, the CSRF rule (Java <c>SessionTests</c>).</summary>
public sealed class SessionTests(SharedApi api) : IClassFixture<SharedApi>
{
    private const string Refresh = "/api/v1/auth/refresh";

    private readonly IdentityTestSupport id = new(api.Factory);

    [Fact]
    public async Task RefreshRotatesTheCookieAndTheNewAccessTokenWorks()
    {
        var session = await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER));

        var refreshed = await id.WithCookieAsync(Refresh, "zawed_rt", session.RefreshToken);

        Assert.Equal(200, (int)refreshed.StatusCode);
        var rotated = Cookie(refreshed, "zawed_rt");
        Assert.NotNull(rotated);
        Assert.NotEqual(session.RefreshToken, rotated);
        var token = (await JsonOf(refreshed))["accessToken"]!.GetValue<string>();
        Assert.Equal(200, (int)(await id.AuthedAsync("GET", "/api/v1/auth/me", token)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentRefreshesWithTheSameCookieDoNotSignTheUserOut()
    {
        var session = await id.SignInAsync(await id.SeedAsync(Role.ACCOUNTANT));

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => id.WithCookieAsync(Refresh, "zawed_rt", session.RefreshToken))));

        foreach (var result in results)
        {
            Assert.True((int)result.StatusCode == 200, await result.Content.ReadAsStringAsync());
        }
        Assert.Null(await id.Owner.QueryOneAsync<DateTime?>("select revoked_at from sessions where user_id = @UserId", new { session.UserId }));
    }

    [Fact]
    public async Task ARotatedAwayTokenUsedAfterTheGraceWindowEndsTheWholeSession()
    {
        var session = await id.SignInAsync(await id.SeedAsync(Role.STATION_MANAGER));
        var first = await id.WithCookieAsync(Refresh, "zawed_rt", session.RefreshToken);
        var current = Cookie(first, "zawed_rt")!;
        var accessToken = (await JsonOf(first))["accessToken"]!.GetValue<string>();

        // Within the grace window, the old token is a race, not a theft.
        Assert.Equal(200, (int)(await id.WithCookieAsync(Refresh, "zawed_rt", session.RefreshToken)).StatusCode);

        await id.BackdateRotationAsync(session.UserId, 120);
        var stolen = await id.WithCookieAsync(Refresh, "zawed_rt", session.RefreshToken);
        Assert.Equal(401, (int)stolen.StatusCode);
        Assert.Equal("REFRESH_REUSE_DETECTED", await CodeOf(stolen));

        // The legitimate holder is signed out too: the session itself is gone.
        Assert.Equal("SESSION_EXPIRED", await CodeOf(await id.WithCookieAsync(Refresh, "zawed_rt", current)));
        Assert.Equal(401, (int)(await id.AuthedAsync("GET", "/api/v1/auth/me", accessToken)).StatusCode);
        Assert.Equal(1L, await id.Owner.QueryOneAsync<long>("""
            select count(*) from audit_log where action = 'SESSION_REVOKED' and entity_id = @UserId and actor_id is null
              and metadata ->> 'reason' = 'REFRESH_REUSE_DETECTED'
            """, new { session.UserId }));
    }

    [Fact]
    public async Task LogoutEndsTheSessionAndItsAccessTokenImmediately()
    {
        var session = await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER));

        var logout = await id.WithCookieAsync("/api/v1/auth/logout", "zawed_rt", session.RefreshToken);

        Assert.Equal(204, (int)logout.StatusCode);
        Assert.Contains(SetCookies(logout), c => c.StartsWith("zawed_rt=;", StringComparison.Ordinal) && c.Contains("Max-Age=0", StringComparison.Ordinal));
        Assert.Equal(401, (int)(await id.AuthedAsync("GET", "/api/v1/auth/me", session.AccessToken)).StatusCode);
        Assert.Equal("SESSION_EXPIRED", await CodeOf(await id.WithCookieAsync(Refresh, "zawed_rt", session.RefreshToken)));
    }

    [Fact]
    public async Task SuspendingAUserEndsTheirAccessAtTheNextRequest()
    {
        var user = await id.SeedAsync(Role.FLEET_MANAGER);
        var session = await id.SignInAsync(user);
        await id.Owner.ExecuteAsync("update users set status = 'SUSPENDED' where id = @Id", new { user.Id });

        var me = await id.AuthedAsync("GET", "/api/v1/auth/me", session.AccessToken);
        Assert.Equal(401, (int)me.StatusCode);
        Assert.Equal("UNAUTHENTICATED", await CodeOf(me));
    }

    [Fact]
    public async Task RefreshAndLogoutRefuseRequestsWithoutOurOriginAndClientHeader()
    {
        var session = await id.SignInAsync(await id.SeedAsync(Role.ACCOUNTANT));

        var noHeader = new HttpRequestMessage(HttpMethod.Post, Refresh);
        noHeader.Headers.Add("Cookie", "zawed_rt=" + session.RefreshToken);
        noHeader.Headers.Add("Origin", WebOrigin);
        var foreignOrigin = new HttpRequestMessage(HttpMethod.Post, Refresh);
        foreignOrigin.Headers.Add("Cookie", "zawed_rt=" + session.RefreshToken);
        foreignOrigin.Headers.Add("Origin", "https://evil.example");
        foreignOrigin.Headers.Add("X-Zawed-Client", "web");
        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add("Cookie", "zawed_rt=" + session.RefreshToken);

        Assert.Equal("CSRF_REJECTED", await CodeOf(await id.Client.SendAsync(noHeader)));
        Assert.Equal(403, (int)(await id.Client.SendAsync(foreignOrigin)).StatusCode);
        Assert.Equal("CSRF_REJECTED", await CodeOf(await id.Client.SendAsync(logout)));
        // Nothing was rotated or ended by the refused calls.
        Assert.Equal(200, (int)(await id.WithCookieAsync(Refresh, "zawed_rt", session.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task ATemporaryPasswordAllowsOnlyChangingIt()
    {
        var user = await id.SeedAsync(Role.FLEET_MANAGER, Scope.Company(Guid.CreateVersion7()), true);
        var first = await id.SignInAsync(user);
        var second = await id.SignInAsync(user);

        var blocked = await id.AuthedAsync("GET", "/api/v1/auth/sessions", first.AccessToken);
        Assert.Equal(403, (int)blocked.StatusCode);
        Assert.Equal("PASSWORD_CHANGE_REQUIRED", await CodeOf(blocked));
        Assert.Equal(200, (int)(await id.AuthedAsync("GET", "/api/v1/auth/me", first.AccessToken)).StatusCode);

        var weak = await id.AuthedAsync("POST", "/api/v1/auth/password", first.AccessToken,
            $"{{\"currentPassword\":\"{Password}\",\"newPassword\":\"short\"}}");
        var weakBody = await JsonOf(weak);
        Assert.Equal("PASSWORD_TOO_WEAK", weakBody["code"]!.GetValue<string>());
        Assert.Equal("newPassword", weakBody["field"]!.GetValue<string>());
        Assert.Equal(12, weakBody["params"]!["minLength"]!.GetValue<int>());

        var changed = await id.AuthedAsync("POST", "/api/v1/auth/password", first.AccessToken,
            $"{{\"currentPassword\":\"{Password}\",\"newPassword\":\"a much longer chosen password\"}}");
        Assert.Equal(204, (int)changed.StatusCode);

        // This session continues; the other one, which knew the old password, is over.
        Assert.Equal(200, (int)(await id.AuthedAsync("GET", "/api/v1/auth/sessions", first.AccessToken)).StatusCode);
        Assert.Equal(401, (int)(await id.AuthedAsync("GET", "/api/v1/auth/me", second.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task ASuperAdminSessionRefreshesAndEndsAtTheOneAuthPath()
    {
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));

        var refreshed = await id.WithCookieAsync(Refresh, "zawed_rt", admin.RefreshToken);
        Assert.Equal(200, (int)refreshed.StatusCode);
        Assert.Contains(SetCookies(refreshed), c => c.StartsWith("zawed_rt=", StringComparison.Ordinal) && c.Contains("Path=/api/v1/auth", StringComparison.Ordinal));
        var rotated = Cookie(refreshed, "zawed_rt")!;

        Assert.Equal(204, (int)(await id.WithCookieAsync("/api/v1/auth/logout", "zawed_rt", rotated)).StatusCode);
        Assert.Equal("SESSION_EXPIRED", await CodeOf(await id.WithCookieAsync(Refresh, "zawed_rt", rotated)));
    }

    [Fact]
    public async Task ASessionStartedAtTheRemovedAdminDoorIsNeverRefreshed()
    {
        // Sessions created before W10 by /admin/auth/login keep door = 'ADMIN'. Even presented at /auth/refresh by hand,
        // such a session is refused, so nothing issued by the removed door outlives it.
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        await id.Owner.ExecuteAsync("update sessions set door = 'ADMIN' where user_id = @UserId", new { admin.UserId });

        var refused = await id.WithCookieAsync(Refresh, "zawed_rt", admin.RefreshToken);
        Assert.Equal(401, (int)refused.StatusCode);
        Assert.Equal("SESSION_EXPIRED", await CodeOf(refused));
        Assert.Contains(SetCookies(refused), c => c.StartsWith("zawed_session=;", StringComparison.Ordinal)); // the page stops trying

        // The removed door's cookie name is not read either.
        Assert.Equal("SESSION_EXPIRED", await CodeOf(await id.WithCookieAsync(Refresh, "zawed_admin_rt", admin.RefreshToken)));

        // Signing out still ends such a session, so the Super Admin can close it from the one door.
        Assert.Equal(204, (int)(await id.WithCookieAsync("/api/v1/auth/logout", "zawed_rt", admin.RefreshToken)).StatusCode);
        Assert.NotNull(await id.Owner.QueryOneAsync<DateTime?>("select revoked_at from sessions where user_id = @UserId", new { admin.UserId }));
    }

    [Fact]
    public async Task ASuperAdminSessionKeepsThePlatformIdleLimit()
    {
        // The door no longer decides anything about a session: the role does.
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        Assert.True(await id.Owner.QueryOneAsync<bool>(
            "select expires_at - created_at <= interval '12 hours' and door = 'USER' from sessions where user_id = @UserId", new { admin.UserId }));
        var scoped = await id.SignInAsync(await id.SeedAsync(Role.ACCOUNTANT));
        Assert.True(await id.Owner.QueryOneAsync<bool>(
            "select expires_at - created_at = interval '7 days' and absolute_expires_at - created_at = interval '30 days' from sessions where user_id = @UserId",
            new { scoped.UserId }));
    }

    [Fact]
    public async Task TheSessionsListMarksTheCurrentOneAndAnotherCanBeEnded()
    {
        // Contract §4 (no Java test of its own: the web's session screen relies on it).
        var user = await id.SeedAsync(Role.FLEET_MANAGER);
        var first = await id.SignInAsync(user);
        var second = await id.SignInAsync(user);

        var list = (await JsonOf(await id.AuthedAsync("GET", "/api/v1/auth/sessions", first.AccessToken))).AsArray();
        Assert.Equal(2, list.Count);
        Assert.Single(list, s => s!["current"]!.GetValue<bool>());
        var other = list.Single(s => !s!["current"]!.GetValue<bool>())!["id"]!.GetValue<string>();
        Assert.True(list.All(s => s!.AsObject().ContainsKey("userAgent"))); // null members are written, as in Java

        Assert.Equal(204, (int)(await id.AuthedAsync("DELETE", $"/api/v1/auth/sessions/{other}", first.AccessToken)).StatusCode);
        Assert.Equal(401, (int)(await id.AuthedAsync("GET", "/api/v1/auth/me", second.AccessToken)).StatusCode);
        Assert.Equal(404, (int)(await id.AuthedAsync("DELETE", $"/api/v1/auth/sessions/{other}", first.AccessToken)).StatusCode);
        Assert.Equal(1L, await id.Owner.QueryOneAsync<long>("""
            select count(*) from audit_log where action = 'SESSION_REVOKED' and actor_id = @Id and metadata ->> 'reason' = 'SIGNED_OUT_ELSEWHERE'
            """, new { user.Id }));
    }
}
