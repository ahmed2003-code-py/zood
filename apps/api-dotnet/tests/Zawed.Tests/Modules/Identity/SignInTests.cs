using System.Diagnostics;
using System.Text.Json.Nodes;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Support.IdentityTestSupport;

namespace Zawed.Tests.Modules.Identity;

/// <summary>Contract §3–§5: sign-in for every role at one path, lockout, MFA, and what the caller can and cannot learn (Java <c>SignInTests</c>).</summary>
public sealed class SignInTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly IdentityTestSupport id = new(api.Factory);

    [Fact]
    public async Task AFleetManagerSignsInAndGetsAStrictHttpOnlyRefreshCookie()
    {
        var user = await id.SeedAsync(Role.FLEET_MANAGER);

        var response = await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, Password));

        Assert.Equal(200, (int)response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("AUTHENTICATED", body["status"]!.GetValue<string>());
        Assert.Equal("FLEET_MANAGER", body["session"]!["user"]!["role"]!.GetValue<string>());
        Assert.Equal("COMPANY", body["session"]!["user"]!["scope"]!["type"]!.GetValue<string>());
        var refreshCookie = SetCookies(response).First(c => c.StartsWith("zawed_rt=", StringComparison.Ordinal));
        Assert.Contains("HttpOnly", refreshCookie, StringComparison.Ordinal);
        Assert.Contains("SameSite=Strict", refreshCookie, StringComparison.Ordinal);
        Assert.Contains("Path=/api/v1/auth", refreshCookie, StringComparison.Ordinal);
        Assert.Contains(SetCookies(response), c => c.StartsWith("zawed_session=1", StringComparison.Ordinal) && !c.Contains("HttpOnly", StringComparison.Ordinal));

        var me = await id.AuthedAsync("GET", "/api/v1/auth/me", body["session"]!["accessToken"]!.GetValue<string>());
        Assert.Equal(200, (int)me.StatusCode);
        Assert.Equal(user.Email, (await JsonOf(me))["email"]!.GetValue<string>());
    }

    [Fact]
    public async Task AWrongPasswordAndAnUnknownEmailLookExactlyTheSame()
    {
        var user = await id.SeedAsync(Role.ACCOUNTANT);

        var wrongPassword = await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, "not the password at all"));
        var unknownEmail = await id.PostAsync("/api/v1/auth/login", LoginBody($"nobody-{Guid.NewGuid()}@zawed.test", Password));

        Assert.Equal(401, (int)wrongPassword.StatusCode);
        Assert.Equal(401, (int)unknownEmail.StatusCode);
        Assert.Equal(await WithoutRequestId(unknownEmail), await WithoutRequestId(wrongPassword));
        Assert.Equal("INVALID_CREDENTIALS", await CodeOf(wrongPassword));
    }

    [Fact]
    public async Task FiveWrongPasswordsLockTheAccountEvenAgainstTheRightOneUntilOpsUnlocksIt()
    {
        var user = await id.SeedAsync(Role.STATION_MANAGER);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("INVALID_CREDENTIALS", await CodeOf(await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, "wrong password " + i))));
        }
        var fifth = await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, "wrong password 5"));
        Assert.Equal(423, (int)fifth.StatusCode);
        Assert.Equal("ACCOUNT_LOCKED", await CodeOf(fifth));

        var rightPassword = await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, Password));
        var body = await JsonOf(rightPassword);
        Assert.Equal("ACCOUNT_LOCKED", body["code"]!.GetValue<string>());
        Assert.True(body["params"]!["retryAfterSeconds"]!.GetValue<long>() > 0);

        var ops = await id.SignInAsync(await id.SeedAsync(Role.OPS));
        Assert.Equal(204, (int)(await id.AuthedAsync("POST", $"/api/v1/admin/users/{user.Id}/unlock", ops.AccessToken)).StatusCode);
        Assert.Equal(200, (int)(await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, Password))).StatusCode);
    }

    [Fact]
    public async Task ASuspendedAccountIsRevealedOnlyAfterTheRightPassword()
    {
        var user = await id.SeedAsync(Role.FLEET_MANAGER);
        await id.Owner.ExecuteAsync("update users set status = 'SUSPENDED' where id = @Id", new { user.Id });

        Assert.Equal("INVALID_CREDENTIALS", await CodeOf(await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, "a wrong password here"))));
        var right = await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, Password));
        Assert.Equal(403, (int)right.StatusCode);
        Assert.Equal("ACCOUNT_DISABLED", await CodeOf(right));
    }

    [Fact]
    public async Task ASuperAdminSignsInAtTheOneSignInPathAndStillNeedsTheSecondFactor()
    {
        // B7 W10 (owner, 2026-11-20): one sign-in page for every role; the role decides the portal.
        var admin = await id.SeedAsync(Role.SUPER_ADMIN);

        var login = await id.PostAsync("/api/v1/auth/login", LoginBody(admin.Email, Password));
        Assert.Equal(200, (int)login.StatusCode);
        var loginBody = await JsonOf(login);
        Assert.Equal("MFA_REQUIRED", loginBody["status"]!.GetValue<string>()); // MFA is on: a password alone is not enough
        Assert.DoesNotContain(SetCookies(login), c => c.StartsWith("zawed_rt=", StringComparison.Ordinal)); // no session before the second factor

        var challenge = loginBody["challengeToken"]!.GetValue<string>();
        HttpResponseMessage verified = null!;
        for (var offset = 0; offset <= 1; offset++)
        {
            verified = await id.PostAsync("/api/v1/auth/mfa/verify", ChallengeCodeBody(challenge, Code(admin.TotpSecret!, offset)));
            if ((int)verified.StatusCode == 200)
            {
                break;
            }
        }
        Assert.Equal(200, (int)verified.StatusCode);
        var body = await JsonOf(verified);
        Assert.Equal("SUPER_ADMIN", body["session"]!["user"]!["role"]!.GetValue<string>());
        Assert.Contains(SetCookies(verified), c => c.StartsWith("zawed_rt=", StringComparison.Ordinal) && c.Contains("Path=/api/v1/auth", StringComparison.Ordinal)
            && c.Contains("HttpOnly", StringComparison.Ordinal) && c.Contains("SameSite=Strict", StringComparison.Ordinal));
        // The Super Admin's own endpoints.
        Assert.Equal(200, (int)(await id.AuthedAsync("GET", "/api/v1/admin/audit", body["session"]!["accessToken"]!.GetValue<string>())).StatusCode);

        // The sign-in is audited as before, now without a door.
        Assert.Equal(1L, await id.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where action = 'LOGIN_SUCCEEDED' and actor_id = @Id and metadata -> 'door' is null", new { admin.Id }));
    }

    [Fact]
    public async Task EveryRoleIsAnsweredLikeAnUnknownEmailUntilThePasswordIsProven()
    {
        // One sign-in path for every role (B7 W10): a wrong password must not tell a caller which role, and so which
        // portal, an email belongs to.
        var unknownEmail = await id.PostAsync("/api/v1/auth/login", LoginBody($"nobody-{Guid.NewGuid()}@zawed.test", Password));
        Assert.Equal(401, (int)unknownEmail.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", await CodeOf(unknownEmail));
        Assert.Empty(SetCookies(unknownEmail));
        var expected = await WithoutRequestId(unknownEmail);

        foreach (var role in Enum.GetValues<Role>())
        {
            id.Limiter.Reset();
            var user = await id.SeedAsync(role);
            var wrongPassword = await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, "not the password at all"));
            Assert.Equal(401, (int)wrongPassword.StatusCode);
            Assert.Equal(expected, await WithoutRequestId(wrongPassword));
            Assert.Empty(SetCookies(wrongPassword));
        }
    }

    [Fact]
    public async Task ARefusalTakesAboutAsLongWhateverTheRoleAndWhetherTheAccountExists()
    {
        // An unknown email still runs a full Argon2 check (PasswordHasher.Burn), so it cannot be told from a wrong
        // password by its timing. Without that, an unknown email answers in a millisecond or two against tens of
        // milliseconds, far outside the bound below. Medians of interleaved rounds keep a busy machine from failing the
        // test; four wrong passwords stay under the lock.
        var emails = new Dictionary<string, string> { ["unknown"] = $"nobody-{Guid.NewGuid()}@zawed.test" };
        foreach (var role in new[] { Role.SUPER_ADMIN, Role.OPS, Role.FLEET_MANAGER, Role.STATION_MANAGER })
        {
            emails[role.ToString()] = (await id.SeedAsync(role)).Email;
        }
        var timings = new Dictionary<string, List<long>>();
        for (var round = 0; round < 4; round++)
        {
            foreach (var (key, email) in emails)
            {
                id.Limiter.Reset();
                var started = Stopwatch.GetTimestamp();
                var refused = await id.PostAsync("/api/v1/auth/login", LoginBody(email, "a wrong password " + round));
                var elapsed = Stopwatch.GetTimestamp() - started;
                Assert.Equal("INVALID_CREDENTIALS", await CodeOf(refused));
                if (round > 0) // the first round warms up the path
                {
                    (timings.TryGetValue(key, out var list) ? list : timings[key] = []).Add(elapsed);
                }
            }
        }
        var medians = timings.ToDictionary(t => t.Key, t => t.Value.Order().ToList()[t.Value.Count / 2]);
        var fastest = medians.Values.Min();
        var slowest = medians.Values.Max();
        Assert.True(slowest < fastest * 3, "median refusal time in ticks per case: " + string.Join(", ", medians.Select(m => $"{m.Key}={m.Value}")));
    }

    [Fact]
    public async Task FiveWrongPasswordsLockEveryRoleAlikeAndTheFailuresAreAudited()
    {
        foreach (var role in Enum.GetValues<Role>())
        {
            id.Limiter.Reset();
            var user = await id.SeedAsync(role);
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal("INVALID_CREDENTIALS", await CodeOf(await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, "wrong password " + i))));
            }
            var fifth = await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, "wrong password 5"));
            Assert.Equal(423, (int)fifth.StatusCode);
            Assert.Equal("ACCOUNT_LOCKED", await CodeOf(fifth));
            var rightPassword = await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, Password));
            Assert.Equal("ACCOUNT_LOCKED", await CodeOf(rightPassword)); // the right password does not open a locked account
            Assert.Empty(SetCookies(rightPassword));
            Assert.Equal(5L, await id.Owner.QueryOneAsync<long>("""
                select count(*) from audit_log
                where action = 'LOGIN_FAILED' and entity_id = @Id and metadata ->> 'reason' = 'WRONG_PASSWORD'
                """, new { user.Id }));
            Assert.Equal(1L, await id.Owner.QueryOneAsync<long>(
                "select count(*) from audit_log where action = 'ACCOUNT_LOCKED' and entity_id = @Id", new { user.Id }));
            Assert.Equal(0L, await id.Owner.QueryOneAsync<long>("select count(*) from sessions where user_id = @Id", new { user.Id }));
        }
    }

    [Fact]
    public async Task NoRoleCanChooseARoleAScopeOrADoorAtSignIn()
    {
        // The one sign-in path takes an email and a password and nothing else: role and scope come from the user's row.
        foreach (var role in Enum.GetValues<Role>().Where(r => r != Role.SUPER_ADMIN))
        {
            id.Limiter.Reset();
            var user = await id.SeedAsync(role);
            var forged = await id.PostAsync("/api/v1/auth/login", "{\"email\":\"" + user.Email + "\",\"password\":\"" + Password
                + "\",\"role\":\"SUPER_ADMIN\",\"door\":\"ADMIN\",\"scope\":{\"type\":\"PLATFORM\"}}");
            Assert.Equal(400, (int)forged.StatusCode); // unknown fields are refused
            Assert.Empty(SetCookies(forged));

            var signedIn = await id.SignInAsync(user);
            var me = await JsonOf(await id.AuthedAsync("GET", "/api/v1/auth/me", signedIn.AccessToken));
            Assert.Equal(role.ToString(), me["role"]!.GetValue<string>());
            Assert.Equal(user.Scope.Type.ToString(), me["scope"]!["type"]!.GetValue<string>());
            Assert.Equal(403, (int)(await id.AuthedAsync("GET", "/api/v1/admin/audit", signedIn.AccessToken)).StatusCode);

            // A refresh re-reads the role from the database: still the same role, still no admin scope.
            var refreshed = await id.WithCookieAsync("/api/v1/auth/refresh", "zawed_rt", signedIn.RefreshToken);
            Assert.Equal(200, (int)refreshed.StatusCode);
            var refreshedBody = await JsonOf(refreshed);
            Assert.Equal(role.ToString(), refreshedBody["user"]!["role"]!.GetValue<string>());
            Assert.Equal(403, (int)(await id.AuthedAsync("GET", "/api/v1/admin/audit", refreshedBody["accessToken"]!.GetValue<string>())).StatusCode);
        }
    }

    [Fact]
    public async Task ASuperAdminWithoutASecondFactorMustEnrolBeforeAnySessionExists()
    {
        var admin = await id.SeedAsync(Role.SUPER_ADMIN);
        await id.Owner.ExecuteAsync("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = @Id", new { admin.Id });

        var login = await id.PostAsync("/api/v1/auth/login", LoginBody(admin.Email, Password));

        Assert.Equal(200, (int)login.StatusCode);
        var text = await login.Content.ReadAsStringAsync();
        Assert.Equal("MFA_ENROLLMENT_REQUIRED", JsonNode.Parse(text)!["status"]!.GetValue<string>());
        Assert.DoesNotContain("accessToken", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SetCookies(login), c => c.StartsWith("zawed_rt=", StringComparison.Ordinal));
        Assert.Equal(0L, await id.Owner.QueryOneAsync<long>("select count(*) from sessions where user_id = @Id", new { admin.Id }));
    }

    [Fact]
    public async Task TheSeparateAdminSignInIsGone()
    {
        var admin = await id.SeedAsync(Role.SUPER_ADMIN);
        foreach (var path in new[] { "/api/v1/admin/auth/login", "/api/v1/admin/auth/refresh", "/api/v1/admin/auth/logout" })
        {
            var response = await id.PostAsync(path, LoginBody(admin.Email, Password));
            Assert.Equal(401, (int)response.StatusCode);
            Assert.Equal("UNAUTHENTICATED", await CodeOf(response));
            Assert.Empty(SetCookies(response));
        }
        Assert.Equal(0L, await id.Owner.QueryOneAsync<long>("select count(*) from sessions where user_id = @Id", new { admin.Id }));
    }

    [Fact]
    public async Task OpsMustEnrolASecondFactorOnFirstSignInAndCannotReplayACode()
    {
        var ops = await id.SeedAsync(Role.OPS);
        await id.Owner.ExecuteAsync("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = @Id", new { ops.Id });

        var login = await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(ops.Email, Password)));
        Assert.Equal("MFA_ENROLLMENT_REQUIRED", login["status"]!.GetValue<string>());
        var challenge = login["challengeToken"]!.GetValue<string>();

        var start = await id.PostAsync("/api/v1/auth/mfa/enroll/start", ChallengeBody(challenge));
        Assert.Equal(200, (int)start.StatusCode);
        var startBody = await JsonOf(start);
        var secretBase32 = startBody["secret"]!.GetValue<string>();
        Assert.StartsWith("otpauth://totp/ZAWED", startBody["otpauthUri"]!.GetValue<string>(), StringComparison.Ordinal);
        // Asking again shows the same key: a phone that already scanned it keeps working.
        Assert.Equal(secretBase32, (await JsonOf(await id.PostAsync("/api/v1/auth/mfa/enroll/start", ChallengeBody(challenge))))["secret"]!.GetValue<string>());

        var code = Code(Base32Decode(secretBase32), 0);
        var confirm = await id.PostAsync("/api/v1/auth/mfa/enroll/confirm", ChallengeCodeBody(challenge, code));
        Assert.Equal(200, (int)confirm.StatusCode);
        var confirmed = await JsonOf(confirm);
        Assert.Equal("AUTHENTICATED", confirmed["status"]!.GetValue<string>());
        Assert.True(confirmed["session"]!["user"]!["mfaEnabled"]!.GetValue<bool>());

        // The same code again, on a new sign-in: refused, because its time step was already used.
        var next = (await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(ops.Email, Password))))["challengeToken"]!.GetValue<string>();
        var replay = await id.PostAsync("/api/v1/auth/mfa/verify", ChallengeCodeBody(next, code));
        Assert.Equal("MFA_INVALID_CODE", await CodeOf(replay));
    }

    [Fact]
    public async Task AChallengeDiesAfterFiveWrongCodes()
    {
        var ops = await id.SeedAsync(Role.OPS);
        var challenge = (await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(ops.Email, Password))))["challengeToken"]!.GetValue<string>();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("MFA_INVALID_CODE", await CodeOf(await id.PostAsync("/api/v1/auth/mfa/verify", ChallengeCodeBody(challenge, "000000"))));
        }
        var right = await id.PostAsync("/api/v1/auth/mfa/verify", ChallengeCodeBody(challenge, Code(ops.TotpSecret!, 0)));
        Assert.Equal("MFA_CHALLENGE_EXPIRED", await CodeOf(right));
        Assert.Equal(5L, await id.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where entity_id = @Id and action = 'MFA_FAILED'", new { ops.Id }));
    }

    [Fact]
    public async Task SignInIsRateLimitedPerAddress()
    {
        HttpResponseMessage last = null!;
        for (var i = 0; i < 11; i++)
        {
            last = await id.PostAsync("/api/v1/auth/login", LoginBody($"probe-{i}@zawed.test", "whatever password"));
        }
        Assert.Equal(429, (int)last.StatusCode);
        var body = await JsonOf(last);
        Assert.Equal("RATE_LIMITED", body["code"]!.GetValue<string>());
        Assert.NotNull(last.Headers.RetryAfter);
        Assert.True(body["retryAfterSeconds"]!.GetValue<int>() > 0);
        Assert.True(body["params"]!["retryAfterSeconds"]!.GetValue<long>() > 0);
        Assert.True(body["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ABearerTokenDoesNotLiftTheSignInLimit()
    {
        // B1 review finding 2: the per-user limit (300/min) must not replace the sign-in one (10/min).
        var someone = await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER));
        id.Limiter.Reset();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 11; i++)
        {
            last = await id.AuthedAsync("POST", "/api/v1/auth/login", someone.AccessToken, LoginBody($"probe-{i}@zawed.test", "whatever password"));
        }
        Assert.Equal(429, (int)last.StatusCode);
        Assert.Equal("RATE_LIMITED", await CodeOf(last));
    }

    [Fact]
    public async Task RefreshIsLimitedPerSessionNotPerAddress()
    {
        // B1 review finding 3: one busy session must not lock out others behind the same address.
        var busy = await id.SignInAsync(await id.SeedAsync(Role.ACCOUNTANT));
        var other = await id.SignInAsync(await id.SeedAsync(Role.ACCOUNTANT));
        id.Limiter.Reset();
        // A stale cookie value keeps one bucket busy without rotating anything.
        var stale = busy.RefreshToken + "-stale";
        for (var i = 0; i < 30; i++)
        {
            await id.WithCookieAsync("/api/v1/auth/refresh", "zawed_rt", stale);
        }
        var blocked = await id.WithCookieAsync("/api/v1/auth/refresh", "zawed_rt", stale);
        Assert.Equal("RATE_LIMITED", await CodeOf(blocked));
        var fine = await id.WithCookieAsync("/api/v1/auth/refresh", "zawed_rt", other.RefreshToken);
        Assert.Equal(200, (int)fine.StatusCode);
    }

    [Fact]
    public async Task AnOldEnrolmentChallengeCannotReplaceAnAuthenticator()
    {
        // B1 review finding 5: the first completed enrolment wins; a second challenge cannot overwrite it.
        var ops = await id.SeedAsync(Role.OPS);
        await id.Owner.ExecuteAsync("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = @Id", new { ops.Id });
        var first = (await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(ops.Email, Password))))["challengeToken"]!.GetValue<string>();
        var second = (await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(ops.Email, Password))))["challengeToken"]!.GetValue<string>();
        var firstSecret = Base32Decode((await JsonOf(await id.PostAsync("/api/v1/auth/mfa/enroll/start", ChallengeBody(first))))["secret"]!.GetValue<string>());
        var secondSecret = Base32Decode((await JsonOf(await id.PostAsync("/api/v1/auth/mfa/enroll/start", ChallengeBody(second))))["secret"]!.GetValue<string>());

        Assert.Equal(200, (int)(await id.PostAsync("/api/v1/auth/mfa/enroll/confirm", ChallengeCodeBody(first, Code(firstSecret, 0)))).StatusCode);
        var late = await id.PostAsync("/api/v1/auth/mfa/enroll/confirm", ChallengeCodeBody(second, Code(secondSecret, 0)));
        Assert.Equal("MFA_CHALLENGE_EXPIRED", await CodeOf(late));
    }

    [Fact]
    public async Task APasswordResetVoidsOpenChallenges()
    {
        var target = await id.SeedAsync(Role.OPS);
        var challenge = (await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(target.Email, Password))))["challengeToken"]!.GetValue<string>();
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        Assert.Equal(200, (int)(await id.AuthedAsync("POST", $"/api/v1/admin/users/{target.Id}/reset-password", admin.AccessToken)).StatusCode);
        var verify = await id.PostAsync("/api/v1/auth/mfa/verify", ChallengeCodeBody(challenge, Code(target.TotpSecret!, 0)));
        Assert.Equal("MFA_CHALLENGE_EXPIRED", await CodeOf(verify));
    }

    [Fact]
    public async Task AScopedUserCanSignInWithoutASecondFactor()
    {
        var user = await id.SeedAsync(Role.FLEET_MANAGER, Scope.Company(Guid.CreateVersion7()), false);
        var login = await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email.ToUpperInvariant(), Password)));
        Assert.Equal("AUTHENTICATED", login["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task AStationManagerNeedsASecondFactorSinceB6()
    {
        // The station manager reads the station's bank account and payouts (roadmap delta A3).
        var user = await id.SeedAsync(Role.STATION_MANAGER, Scope.Operator(Guid.CreateVersion7()), false);
        var status = (await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(user.Email, Password))))["status"]!.GetValue<string>();
        Assert.Contains(status, new[] { "MFA_REQUIRED", "MFA_ENROLLMENT_REQUIRED" });
    }
}
