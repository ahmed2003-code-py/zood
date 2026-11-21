using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.RateLimit;

namespace Zawed.Api.Modules.Identity;

/// <summary>
/// The refresh cookie (contract §3; Java <c>AuthCookies</c>): HttpOnly so no script can read it, Secure when deployed,
/// SameSite=Strict so no other site can make the browser send it, and scoped to the auth path so it travels with
/// refresh and logout and nothing else. One cookie for every role since B7 W10 (ADR 0008 amendment).
/// <para>Beside it, <c>zawed_session=1</c> is readable by the page and holds no secret: it only tells the web app,
/// after a reload, that a refresh is worth trying.</para>
/// <para>The header is written in Spring's <c>ResponseCookie</c> form, attribute for attribute
/// (<c>Path</c>, <c>Max-Age</c>, <c>Expires</c>, <c>Secure</c>, <c>HttpOnly</c>, <c>SameSite</c>), not in ASP.NET Core's
/// lower-case one, so the two APIs send the same bytes.</para>
/// </summary>
public sealed class AuthCookies(IOptions<ZawedOptions> options, TimeProvider clock)
{
    public const string UserCookie = SecurityLimits.RefreshCookie;
    public const string HintCookie = "zawed_session";
    private const string AuthPath = "/api/v1/auth";

    private readonly bool secure = options.Value.Security.CookieSecure;

    public void Set(HttpResponse response, string refreshToken)
    {
        var lifetime = Sessions.Absolute;
        response.Headers.Append("Set-Cookie", Cookie(UserCookie, refreshToken, AuthPath, true, lifetime));
        response.Headers.Append("Set-Cookie", Cookie(HintCookie, "1", "/", false, lifetime));
    }

    public void Clear(HttpResponse response)
    {
        response.Headers.Append("Set-Cookie", Cookie(UserCookie, "", AuthPath, true, TimeSpan.Zero));
        response.Headers.Append("Set-Cookie", Cookie(HintCookie, "", "/", false, TimeSpan.Zero));
    }

    public static string? Read(HttpRequest request) => request.Cookies.TryGetValue(UserCookie, out var value) ? value : null;

    private string Cookie(string name, string value, string path, bool httpOnly, TimeSpan maxAge)
    {
        var seconds = (long)maxAge.TotalSeconds;
        var expires = seconds > 0 ? clock.GetUtcNow() + maxAge : DateTimeOffset.UnixEpoch;
        var cookie = new StringBuilder().Append(name).Append('=').Append(value)
            .Append("; Path=").Append(path)
            .Append("; Max-Age=").Append(seconds.ToString(CultureInfo.InvariantCulture))
            .Append("; Expires=").Append(expires.UtcDateTime.ToString("ddd, dd MMM yyyy HH':'mm':'ss 'GMT'", CultureInfo.InvariantCulture));
        if (secure)
        {
            cookie.Append("; Secure");
        }
        if (httpOnly)
        {
            cookie.Append("; HttpOnly");
        }
        return cookie.Append("; SameSite=Strict").ToString();
    }
}

/// <summary>
/// The defence for the endpoints that act on the refresh cookie, refresh and logout (Java <c>CsrfRule</c>). The
/// cookie is SameSite=Strict, so another site cannot make the browser send it. As a second, independent layer,
/// these endpoints also require the browser's <c>Origin</c> to be one of ours and a custom header that a cross-site
/// form or image request cannot set.
/// </summary>
public sealed class CsrfRule(IOptions<ZawedOptions> options)
{
    public const string ClientHeader = "X-Zawed-Client";

    private readonly IReadOnlyList<string> allowedOrigins = options.Value.Cors.Origins();

    /// <summary>True when the request may act on the refresh cookie.</summary>
    public bool Accepts(HttpRequest request)
    {
        // The first value of each header, as the servlet's getHeader reads it.
        var origin = request.Headers.Origin.FirstOrDefault();
        return request.Headers[ClientHeader].FirstOrDefault() == "web" && origin is not null && allowedOrigins.Contains(origin);
    }
}
