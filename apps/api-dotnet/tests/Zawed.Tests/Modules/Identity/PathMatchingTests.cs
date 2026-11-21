using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Support.IdentityTestSupport;

namespace Zawed.Tests.Modules.Identity;

/// <summary>
/// B7 "path matching" (Java <c>PathMatchingTests</c>): our own filters (the sign-in limit, the password-change gate)
/// must see a request path exactly as routing does. Routing matches decoded segments, so <c>/api/v1/auth/logi%6E</c> is
/// the sign-in endpoint to it while a raw string comparison sees an unknown path. Every path whose raw form is not
/// already canonical is therefore refused before any filter or controller looks at it, and routing is as strict as
/// Spring's (case and trailing slash, <c>StrictRouting</c>). The POS half (wave 2B): an encoded POS path is refused
/// before the POS chain, and a user token on a POS route is refused by it.
/// <para>Requests go over a real socket and are written byte for byte: <c>HttpClient</c> would unescape <c>%6E</c> to
/// <c>n</c> before sending.</para>
/// </summary>
public sealed class PathMatchingTests(KestrelApi api) : IClassFixture<KestrelApi>
{
    /// <summary>The sign-in steps, each with one letter percent-encoded (an unreserved character: legal HTTP).</summary>
    private static readonly string[] EncodedSignIn =
    [
        "/api/v1/auth/logi%6E", "/api/v1/auth/%6Cogin", "/api/v1/auth/logi%6e", "/api/v1/%61uth/login", "/api/v1/auth/mfa/verif%79",
        "/api/v1/auth/mfa/enroll/star%74", "/api/v1/auth/mfa/enroll/confir%6D", "/api/v1/setup/super-admi%6E", "/api/v1/auth/refres%68",
    ];

    private readonly InMemoryRateLimiter limiter = Reset(api);

    private static InMemoryRateLimiter Reset(KestrelApi api)
    {
        var limiter = api.Factory.Services.GetRequiredService<InMemoryRateLimiter>();
        limiter.Reset();
        return limiter;
    }

    [Fact]
    public async Task AnEncodedSignInPathCannotSpendMoreThanTheSignInLimit()
    {
        // The plain path's budget is spent first (10 per minute per address, contract §6).
        for (var i = 0; i < SecurityLimits.SignInPerMinute; i++)
        {
            Assert.Equal("INVALID_CREDENTIALS", (await SendAsync("POST", "/api/v1/auth/login", LoginBody($"probe-{i}@zawed.test", "whatever password"))).Code);
        }
        Assert.Equal("RATE_LIMITED", (await SendAsync("POST", "/api/v1/auth/login", LoginBody("probe-x@zawed.test", "whatever password"))).Code);

        // The same endpoint under another spelling must not open a fresh, unlimited door.
        for (var i = 0; i < 2 * SecurityLimits.SignInPerMinute; i++)
        {
            var encoded = await SendAsync("POST", "/api/v1/auth/logi%6E", LoginBody($"spray-{i}@zawed.test", "whatever password"));
            Assert.Equal(400, encoded.Status);
            Assert.Equal("PATH_NOT_CANONICAL", encoded.Code);
        }
    }

    [Fact]
    public async Task NoEncodedSpellingOfASignInStepReachesItsController()
    {
        foreach (var path in EncodedSignIn)
        {
            limiter.Reset();
            var response = await SendAsync("POST", path, LoginBody("probe@zawed.test", "whatever password"));
            Assert.True(response.Status == 400, $"{path}: {response.Body}");
            Assert.Equal("PATH_NOT_CANONICAL", response.Code);
        }
    }

    [Fact]
    public async Task TheRefusalIsTheStandardEnvelopeWithTheRequestId()
    {
        var response = await SendAsync("POST", "/api/v1/auth/logi%6E", LoginBody("probe@zawed.test", "whatever"),
            ("X-Request-Id", "path-probe-1"));

        Assert.Equal(400, response.Status);
        Assert.Contains("Content-Type: application/json", response.Head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"code\":\"PATH_NOT_CANONICAL\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("\"requestId\":\"path-probe-1\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("\"retryable\":false", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePasswordChangeGateCannotBeWalkedAroundWithAnEncodedPath()
    {
        var id = new IdentityTestSupport(api.Factory);
        var user = await id.SeedAsync(Role.FLEET_MANAGER, Scope.Company(Guid.CreateVersion7()), true);
        var token = await TokenAsync(user);

        // The gate as designed: me is allowed, anything else is refused until the password changes.
        Assert.Equal(200, (await SendAsync("GET", "/api/v1/auth/me", null, Bearer(token))).Status);
        Assert.Equal("PASSWORD_CHANGE_REQUIRED", (await SendAsync("GET", "/api/v1/auth/sessions", null, Bearer(token))).Code);

        foreach (var path in new[] { "/api/v1/auth/session%73", "/api/v1/auth/m%65", "/api/v1/%61uth/sessions" })
        {
            var response = await SendAsync("GET", path, null, Bearer(token));
            Assert.True(response.Status == 400, $"{path}: {response.Body}");
            Assert.Equal("PATH_NOT_CANONICAL", response.Code);
        }
    }

    [Fact]
    public async Task AnEncodedPosPathNeitherSkipsTheDeviceCheckNorLeavesThePosChain()
    {
        // The two credential-obtaining POS calls are public by their exact path only.
        var activate = await SendAsync("POST", "/api/v1/pos/activat%65", "{\"deviceCode\":\"X\",\"activationCode\":\"Y\",\"publicKey\":\"Z\"}");
        Assert.Equal(400, activate.Status);
        Assert.Equal("PATH_NOT_CANONICAL", activate.Code);
        // A user token must never reach a POS controller, however the path is spelled.
        var token = await TokenAsync(await new IdentityTestSupport(api.Factory).SeedAsync(Role.FLEET_MANAGER));
        var plain = await SendAsync("GET", "/api/v1/pos/snapshot", null, Bearer(token));
        Assert.True(plain.Status == 401, plain.Body);
        Assert.Equal("DEVICE_UNAUTHENTICATED", plain.Code);
        foreach (var path in new[] { "/api/v1/po%73/snapshot", "/api/v1/%70os/snapshot" })
        {
            var response = await SendAsync("GET", path, null, Bearer(token));
            Assert.True(response.Status == 400, $"{path}: {response.Body}");
            Assert.Equal("PATH_NOT_CANONICAL", response.Code);
        }
        // Another case is another path, outside the POS chain as in Spring: the user is signed in, and no route matches.
        Assert.Equal(404, (await SendAsync("GET", "/api/v1/POS/snapshot", null, Bearer(token))).Status);
    }

    [Fact]
    public async Task AnEncodedPublicPathIsRefusedLikeAnyOther()
    {
        Assert.Equal(200, (await SendAsync("GET", "/api/v1/health", null)).Status);
        var response = await SendAsync("GET", "/api/v1/healt%68", null);
        Assert.Equal(400, response.Status);
        Assert.Equal("PATH_NOT_CANONICAL", response.Code);
    }

    [Fact]
    public async Task OtherNonCanonicalSpellingsOfTheSignInPathAreRefused()
    {
        // Matrix parameters, empty, dot and dot-dot segments, an encoded slash, dot or percent, a backslash. Some are
        // refused by Kestrel before our middleware runs; the rule is that none of them reaches the sign-in step.
        string[] paths =
        [
            "/api/v1/auth/login;x=1", "/api/v1/auth;x=1/login", "/api/v1//auth/login", "/api/v1/auth/./login", "/api/v1/x/../auth/login",
            "/api/v1/auth%2Flogin", "/api/v1/auth/%2E/login", "/api/v1/auth/logi%256E", "/api/v1/auth%5Clogin",
        ];
        foreach (var path in paths)
        {
            limiter.Reset();
            var response = await SendAsync("POST", path, LoginBody("probe@zawed.test", "whatever password"));
            Assert.True(response.Status == 400, $"{path}: {response.Status} {response.Body}");
        }
    }

    [Fact]
    public async Task ATrailingSlashOrAnotherCaseIsADifferentPathEverywhere()
    {
        // Plain characters: Spring treated these as other paths (no trailing slash match, case-sensitive), and so does
        // StrictRouting. They are not the public sign-in step, so a caller without a token is refused as for any
        // protected route.
        foreach (var path in new[] { "/api/v1/auth/login/", "/api/v1/auth/LOGIN", "/api/v1/Auth/login" })
        {
            var response = await SendAsync("POST", path, LoginBody("probe@zawed.test", "whatever password"));
            Assert.True(response.Status == 401, $"{path}: {response.Status} {response.Body}");
            Assert.Equal("UNAUTHENTICATED", response.Code);
        }
        // With a token they reach no controller either.
        limiter.Reset();
        var token = await TokenAsync(await new IdentityTestSupport(api.Factory).SeedAsync(Role.FLEET_MANAGER));
        foreach (var path in new[] { "/api/v1/auth/login/", "/api/v1/auth/LOGIN" })
        {
            var response = await SendAsync("POST", path, LoginBody("probe@zawed.test", "whatever"), Bearer(token));
            Assert.True(response.Status == 404, $"{path}: {response.Status} {response.Body}");
        }
    }

    [Fact]
    public async Task AQueryStringMayStillCarryEncodedCharacters()
    {
        // Only the path must be canonical: query values (a receipt signature, a search) are decoded as usual. Java probed
        // the receipt link, which arrives with wave 3A; a search on the users list shows the same.
        var token = await TokenAsync(await new IdentityTestSupport(api.Factory).SeedAsync(Role.SUPER_ADMIN));
        var response = await SendAsync("GET", "/api/v1/admin/users?q=a%2Bb%3D", null, Bearer(token));
        Assert.True(response.Status == 200, response.Body);
    }

    // -------------------------------------------------------------------------------------------- helpers

    private sealed record Raw(int Status, string Head, string Body)
    {
        public string? Code => Body.Length > 0 && Body.TrimStart().StartsWith('{') ? JsonNode.Parse(Body)?["code"]?.GetValue<string>() : null;
    }

    private static (string, string) Bearer(string token) => ("Authorization", "Bearer " + token);

    /// <summary>Signs in through the plain path (with the second factor where the role needs one) and returns the access token.</summary>
    private async Task<string> TokenAsync(SeededUser user)
    {
        var secret = user.TotpSecret;
        var login = JsonNode.Parse((await SendAsync("POST", "/api/v1/auth/login", LoginBody(user.Email, Password))).Body)!;
        if (secret is not null)
        {
            var challenge = login["challengeToken"]!.GetValue<string>();
            for (var offset = 0; offset <= 1; offset++)
            {
                var verified = await SendAsync("POST", "/api/v1/auth/mfa/verify", ChallengeCodeBody(challenge, Code(secret, offset)));
                if (verified.Status == 200)
                {
                    return JsonNode.Parse(verified.Body)!["session"]!["accessToken"]!.GetValue<string>();
                }
            }
            throw new InvalidOperationException("second factor refused");
        }
        return login["session"]!["accessToken"]!.GetValue<string>();
    }

    /// <summary>One raw HTTP/1.1 request, the target written exactly as given.</summary>
    private async Task<Raw> SendAsync(string method, string target, string? body, params (string Name, string Value)[] headers)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(api.Address.Host, api.Address.Port);
        await using var stream = tcp.GetStream();
        var request = new StringBuilder().Append($"{method} {target} HTTP/1.1\r\nHost: {api.Address.Authority}\r\nConnection: close\r\n");
        foreach (var (name, value) in headers)
        {
            request.Append($"{name}: {value}\r\n");
        }
        if (body is not null)
        {
            request.Append($"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
        }
        else if (method != "GET")
        {
            request.Append("Content-Length: 0\r\n");
        }
        request.Append("\r\n").Append(body);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToString()));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var content = split < 0 ? "" : text[(split + 4)..];
        if (head.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
        {
            content = Dechunk(content);
        }
        return new Raw(int.Parse(text.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture), head, content);
    }

    private static string Dechunk(string chunked)
    {
        var output = new StringBuilder();
        var position = 0;
        while (position < chunked.Length)
        {
            var lineEnd = chunked.IndexOf("\r\n", position, StringComparison.Ordinal);
            var size = Convert.ToInt32(chunked[position..lineEnd].Split(';')[0], 16);
            if (size == 0)
            {
                break;
            }
            output.Append(chunked, lineEnd + 2, size);
            position = lineEnd + 2 + size + 2;
        }
        return output.ToString();
    }
}
