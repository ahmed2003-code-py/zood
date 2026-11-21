using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Platform.RateLimit;

/// <summary>
/// The request limits of the identity contract §6 (Java <c>SecurityFilters.rateLimits</c>), run after the access
/// token is checked and after the password-change gate, before authorisation:
/// <list type="bullet">
/// <item>the anonymous sign-in steps: 10 per minute per address, whatever the method;</item>
/// <item>refresh: 30 per minute per session (the hash of the cookie) and 300 per minute per address;</item>
/// <item>any signed-in request: 300 per minute per user.</item>
/// </list>
/// By path first, whoever calls: a bearer token on a sign-in step must not trade the sign-in limit for the far
/// larger per-user one (B1 review finding 2). Paths are matched on <see cref="HttpRequest.Path"/>, the decoded
/// path routing uses (Java matched with Spring's own matcher for the same reason, B7 "path matching"), and
/// <see cref="CanonicalPath"/> has already refused every other spelling, so each layer holds alone.
/// </summary>
public static class SecurityLimits
{
    public const int SignInPerMinute = 10;
    public const int RefreshPerMinute = 30;
    public const int RefreshPerAddressPerMinute = 300;
    public const int AuthenticatedPerMinute = 300;

    /// <summary>The refresh cookie (identity <c>AuthCookies</c>).</summary>
    public const string RefreshCookie = "zawed_rt";

    /// <summary>One sign-in path for every role, the Super Admin included, since B7 W10 (ADR 0008 amendment).</summary>
    private static readonly string[] SignInPaths =
    [
        "/api/v1/auth/login", "/api/v1/auth/mfa/verify", "/api/v1/auth/mfa/enroll/start", "/api/v1/auth/mfa/enroll/confirm",
        "/api/v1/setup/super-admin",
    ];

    private const string RefreshPath = "/api/v1/auth/refresh";

    public static IApplicationBuilder UseZawedRateLimits(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var limiter = context.RequestServices.GetRequiredService<IRateLimiter>();
        try
        {
            Apply(context, limiter);
        }
        catch (RateLimitedException limited)
        {
            await ApiErrors.WriteAsync(context, 429, limited.Code, limited.Message, @params: limited.Params,
                retryAfterSeconds: (int)Math.Min(int.MaxValue, limited.RetryAfterSeconds), retryable: true);
            return;
        }
        await next(context);
    });

    /// <summary>Counts the request against every limit that applies to it; throws when one is exceeded.</summary>
    public static void Apply(HttpContext context, IRateLimiter limiter)
    {
        var path = context.Request.Path.Value ?? "";
        if (SignInPaths.Contains(path, StringComparer.Ordinal))
        {
            limiter.Acquire("signin:" + AddressKey(context), SignInPerMinute, TimeSpan.FromMinutes(1));
        }
        else if (string.Equals(path, RefreshPath, StringComparison.Ordinal))
        {
            // Per session, as the contract says: many users share one address behind carrier NAT (ADR 0012).
            // A looser per-address bucket stops a caller inventing cookies.
            var cookie = context.Request.Cookies[RefreshCookie];
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                limiter.Acquire("refresh:" + SecureTokens.Sha256Hex(cookie), RefreshPerMinute, TimeSpan.FromMinutes(1));
            }
            limiter.Acquire("refresh-ip:" + AddressKey(context), RefreshPerAddressPerMinute, TimeSpan.FromMinutes(1));
        }
        if (ZawedPrincipal.From(context.User) is { } principal)
        {
            limiter.Acquire("user:" + principal.UserId, AuthenticatedPerMinute, TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>The address is hashed: rate-limit keys sit in memory and must not become a log of client IPs.</summary>
    private static string AddressKey(HttpContext context) => SecureTokens.Sha256Hex(ClientInfo.Address(context) ?? "null");
}
