using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Identity;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;

namespace Zawed.Tests.Support;

/// <summary>A seeded user and, for MFA roles, the TOTP secret their authenticator would hold.</summary>
public sealed record SeededUser(Guid Id, string Email, Role Role, Scope Scope, byte[]? TotpSecret);

/// <summary>An access token and the refresh cookie value from a completed sign-in.</summary>
public sealed record SignedIn(string AccessToken, string RefreshToken, Guid UserId);

/// <summary>
/// Seeds users straight into the database (as its owner) and drives the real sign-in flow over HTTP (Java
/// <c>IdentityTestSupport</c>). Every test gets fresh users with unique emails, so tests never depend on each other's
/// data. Create one per test (in the test class's constructor): it forgets the application's rate-limit counters, as
/// the Java <c>@BeforeEach</c> did, so a test starts with a full sign-in budget. Later waves use it to get a client
/// with a bearer token for any role.
/// </summary>
public sealed class IdentityTestSupport
{
    public const string Password = "correct horse battery staple";
    public const string WebOrigin = "http://localhost:5173";

    private static int sequence;
    private static readonly Lazy<string> PasswordHash = new(() => new PasswordHasher().Hash(Password));

    public IdentityTestSupport(ZawedApiFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        Limiter = factory.Services.GetRequiredService<InMemoryRateLimiter>();
        Limiter.Reset();
    }

    public ZawedApiFactory Factory { get; }
    public HttpClient Client { get; }
    public InMemoryRateLimiter Limiter { get; }
    public Owner Owner => Factory.Owner;

    public Task<SeededUser> SeedAsync(Role role) =>
        SeedAsync(role, role.IsPlatform() ? Scope.Platform : new Scope(role.ScopeType(), Guid.CreateVersion7()), false);

    public async Task<SeededUser> SeedAsync(Role role, Scope scope, bool mustChangePassword)
    {
        var id = Guid.CreateVersion7();
        var email = $"{role.ToString().ToLowerInvariant()}-{Interlocked.Increment(ref sequence)}-{id.ToString()[..8]}@zawed.test";
        var secret = role.RequiresMfa() ? Totp.NewSecret() : null;
        var encryptor = Factory.Services.GetRequiredService<DataEncryptor>();
        await Owner.ExecuteAsync("""
            insert into users (id, email, full_name, password_hash, role, scope_type, scope_id, status,
                               must_change_password, mfa_secret_encrypted, mfa_enabled, created_at, updated_at)
            values (@id, @email, @name, @hash, @role, @scopeType, @scopeId, 'ACTIVE', @mustChangePassword, @secret, @enabled, now(), now())
            """, new
        {
            id, email, name = "Test " + role, hash = PasswordHash.Value, role = role.ToString(), scopeType = scope.Type.ToString(),
            scopeId = scope.Id, mustChangePassword, secret = secret is null ? null : encryptor.Encrypt(secret, SignIn.UserBinding(id)),
            enabled = secret is not null,
        });
        return new SeededUser(id, email, role, scope, secret);
    }

    /// <summary>A company row with its empty wallet, written as the owner (row-level security does not apply).</summary>
    public async Task<Guid> SeedCompanyAsync(long lowBalanceThresholdPiastres = 0)
    {
        var id = Guid.CreateVersion7();
        await Owner.ExecuteAsync("""
            insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes,
                                   low_balance_threshold_piastres, created_at, updated_at)
            values (@id, 'Test Company', 'شركة اختبار', 'Test Company', 'ACTIVE', 180, @threshold, now(), now())
            """, new { id, threshold = lowBalanceThresholdPiastres });
        // What the ledger module does when tenancy announces a company (V6 backfills existing ones).
        var account = Guid.CreateVersion7();
        await Owner.ExecuteAsync("""
            insert into ledger_accounts (id, code, kind, company_id, station_id, created_at)
            values (@account, @code, 'COMPANY_WALLET', @id, null, now())
            """, new { account, code = $"company:{id}:wallet", id });
        await Owner.ExecuteAsync("""
            insert into company_wallets (company_id, account_id, balance_piastres, created_at, updated_at)
            values (@id, @account, 0, now(), now())
            """, new { id, account });
        return id;
    }

    /// <summary>The current code, or the next step's when <paramref name="stepOffset"/> is 1 (one use per step).</summary>
    public static string Code(byte[] secret, int stepOffset) =>
        Totp.CodeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + stepOffset);

    /// <summary>Signs in at the one sign-in path, answering the TOTP step when the role needs one.</summary>
    public async Task<SignedIn> SignInAsync(SeededUser user)
    {
        var login = await PostAsync("/api/v1/auth/login", LoginBody(user.Email, Password));
        var last = login;
        if (user.TotpSecret is not null)
        {
            var loginBody = await login.Content.ReadAsStringAsync();
            Assert.True(loginBody.Contains("\"MFA_REQUIRED\"", StringComparison.Ordinal), $"sign-in of {user.Role}: {loginBody}");
            var challenge = JsonNode.Parse(loginBody)!["challengeToken"]!.GetValue<string>();
            // Each sign-in of the same user needs a fresh step; walk forward from the current one.
            for (var offset = 0; offset <= 1; offset++)
            {
                last = await PostAsync("/api/v1/auth/mfa/verify", ChallengeCodeBody(challenge, Code(user.TotpSecret, offset)));
                if ((int)last.StatusCode == 200)
                {
                    break;
                }
            }
        }
        var body = await last.Content.ReadAsStringAsync();
        Assert.True((int)last.StatusCode == 200, body);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("AUTHENTICATED", json["status"]!.GetValue<string>());
        return new SignedIn(json["session"]!["accessToken"]!.GetValue<string>(), Cookie(last, AuthCookies.UserCookie)!, user.Id);
    }

    /// <summary>A client whose every request carries the user's bearer token.</summary>
    public async Task<HttpClient> ClientForAsync(SeededUser user)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await SignInAsync(user)).AccessToken);
        return client;
    }

    public static string LoginBody(string email, string password) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["email"] = email, ["password"] = password });

    public static string ChallengeBody(string challenge) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["challengeToken"] = challenge });

    public static string ChallengeCodeBody(string challenge, string code) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["challengeToken"] = challenge, ["code"] = code });

    public Task<HttpResponseMessage> PostAsync(string path, string json) =>
        Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public Task<HttpResponseMessage> GetAsync(string path) => Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

    public Task<HttpResponseMessage> AuthedAsync(string method, string path, string token, string? json = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return Client.SendAsync(request);
    }

    /// <summary>A refresh or logout as the web app sends it: the cookie, our Origin and the client header.</summary>
    public Task<HttpResponseMessage> WithCookieAsync(string path, string cookieName, string value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", $"{cookieName}={value}");
        request.Headers.Add("Origin", WebOrigin);
        request.Headers.Add("X-Zawed-Client", "web");
        return Client.SendAsync(request);
    }

    /// <summary>The value of a cookie the response sets, or null when it is absent or cleared.</summary>
    public static string? Cookie(HttpResponseMessage response, string name) =>
        SetCookies(response).Where(h => h.StartsWith(name + "=", StringComparison.Ordinal))
            .Select(h => h[(name.Length + 1)..h.IndexOf(';', StringComparison.Ordinal)])
            .FirstOrDefault(v => v.Length > 0);

    public static IReadOnlyList<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];

    public static async Task<string> CodeOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["code"]!.GetValue<string>();

    public static async Task<JsonNode> JsonOf(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    public Task BackdateRotationAsync(Guid userId, int seconds) =>
        Owner.ExecuteAsync("update sessions set rotated_at = rotated_at - make_interval(secs => @seconds) where user_id = @userId",
            new { seconds, userId });

    /// <summary>The body without its request id, which differs per request.</summary>
    public static async Task<string> WithoutRequestId(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Remove("requestId");
        return body.ToJsonString();
    }

    /// <summary>RFC 4648 base32 without padding, as the enrolment shows the key.</summary>
    public static byte[] Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new byte[value.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output[index++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }
        return output;
    }
}
