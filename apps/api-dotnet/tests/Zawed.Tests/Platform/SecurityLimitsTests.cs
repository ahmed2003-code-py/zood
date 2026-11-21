using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Time.Testing;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Tests.Platform;

/// <summary>
/// The rate-limit half of the Java <c>SecurityFiltersPathMatchingTests</c> (the second layer of B7 "path matching"):
/// even for a request that reached them with a non-canonical raw target (were <c>CanonicalPath</c> ever removed), the
/// limits read the path as routing reads it, decoded, not the raw string. The password-change gate's half is covered
/// end to end by <c>PathMatchingTests</c>; the POS half comes with the POS scheme (wave 2B). Also the in-memory
/// limiter's window arithmetic (Java <c>InMemoryRateLimiter</c>).
/// </summary>
public sealed class SecurityLimitsTests
{
    private sealed class Recorder : IRateLimiter
    {
        public List<(string Key, int Limit)> Keys { get; } = [];

        public void Acquire(string key, int limit, TimeSpan window) => Keys.Add((key, limit));
    }

    [Theory]
    [InlineData("/api/v1/auth/login", "/api/v1/auth/login")]
    [InlineData("/api/v1/auth/logi%6E", "/api/v1/auth/login")]
    [InlineData("/api/v1/%61uth/login", "/api/v1/auth/login")]
    [InlineData("/api/v1/auth/mfa/verif%79", "/api/v1/auth/mfa/verify")]
    [InlineData("/api/v1/setup/super-admi%6E", "/api/v1/setup/super-admin")]
    public void TheSignInLimitCountsAnEncodedSpellingOfTheSignInPath(string raw, string routed)
    {
        var recorder = new Recorder();
        SecurityLimits.Apply(Request("POST", raw, routed), recorder);
        var (key, limit) = Assert.Single(recorder.Keys);
        Assert.StartsWith("signin:", key, StringComparison.Ordinal);
        Assert.Equal(10, limit);
    }

    [Fact]
    public void TheSignInLimitAppliesWhateverTheMethod()
    {
        var recorder = new Recorder();
        SecurityLimits.Apply(Request("GET", "/api/v1/auth/login", "/api/v1/auth/login"), recorder);
        Assert.StartsWith("signin:", Assert.Single(recorder.Keys).Key, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefreshLimitCountsAnEncodedSpellingOfTheRefreshPath()
    {
        var recorder = new Recorder();
        SecurityLimits.Apply(Request("POST", "/api/v1/auth/refres%68", "/api/v1/auth/refresh"), recorder);
        Assert.StartsWith("refresh-ip:", Assert.Single(recorder.Keys).Key, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefreshWithACookieIsCountedPerSessionAndPerAddress()
    {
        var recorder = new Recorder();
        var context = Request("POST", "/api/v1/auth/refresh", "/api/v1/auth/refresh");
        context.Request.Headers.Cookie = "zawed_rt=abc";
        SecurityLimits.Apply(context, recorder);
        Assert.Equal(["refresh:" + Zawed.Api.Platform.Crypto.SecureTokens.Sha256Hex("abc"), "refresh-ip:"],
            recorder.Keys.Select(k => k.Key.StartsWith("refresh-ip:", StringComparison.Ordinal) ? "refresh-ip:" : k.Key));
        Assert.Equal([30, 300], recorder.Keys.Select(k => k.Limit));
    }

    [Fact]
    public void ASignedInCallerIsCountedPerUserAndStillByTheSignInPath()
    {
        var recorder = new Recorder();
        var user = Guid.CreateVersion7();
        var context = Request("POST", "/api/v1/auth/login", "/api/v1/auth/login");
        context.User = new ZawedPrincipal(user, Guid.CreateVersion7(), Role.FLEET_MANAGER, Scope.Company(Guid.CreateVersion7()), false).ToClaimsPrincipal();
        SecurityLimits.Apply(context, recorder);
        Assert.Equal([("signin:", 10), ("user:" + user, 300)],
            recorder.Keys.Select(k => (k.Key.StartsWith("signin:", StringComparison.Ordinal) ? "signin:" : k.Key, k.Limit)));
    }

    [Fact]
    public void TheAddressIsHashedInTheKey()
    {
        var recorder = new Recorder();
        var context = Request("POST", "/api/v1/auth/login", "/api/v1/auth/login");
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        SecurityLimits.Apply(context, recorder);
        Assert.Equal("signin:" + Zawed.Api.Platform.Crypto.SecureTokens.Sha256Hex("192.0.2.10"), Assert.Single(recorder.Keys).Key);
    }

    [Fact]
    public void TheInMemoryLimiterRefusesTheAttemptAfterTheLimitUntilTheWindowEnds()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-21T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var limiter = new InMemoryRateLimiter(clock);
        for (var i = 0; i < 3; i++)
        {
            limiter.Acquire("k", 3, TimeSpan.FromMinutes(1));
        }
        clock.Advance(TimeSpan.FromSeconds(20.5));
        var refused = Assert.Throws<RateLimitedException>(() => limiter.Acquire("k", 3, TimeSpan.FromMinutes(1)));
        Assert.Equal(40, refused.RetryAfterSeconds); // 39.5 s left, rounded up
        Assert.Equal(429, refused.Status);
        Assert.Equal("RATE_LIMITED", refused.Code);
        Assert.Equal(40L, refused.Params!["retryAfterSeconds"]);
        limiter.Acquire("other", 3, TimeSpan.FromMinutes(1)); // keys are independent

        clock.Advance(TimeSpan.FromSeconds(40));
        limiter.Acquire("k", 3, TimeSpan.FromMinutes(1)); // a new window
    }

    private static DefaultHttpContext Request(string method, string rawTarget, string routedPath)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = routedPath; // what Kestrel hands routing: the decoded path
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget;
        Assert.Equal(rawTarget, CanonicalPath.RawPath(context));
        return context;
    }
}
