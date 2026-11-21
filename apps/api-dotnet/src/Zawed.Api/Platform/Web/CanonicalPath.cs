using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;

namespace Zawed.Api.Platform.Web;

/// <summary>
/// Refuses every request whose path is not already in its one canonical spelling, with
/// <c>400 PATH_NOT_CANONICAL</c> (Java <c>CanonicalPathFilter</c>, ADR 0012 amendment "path matching").
/// Routing matches decoded segments, so <c>/api/v1/auth/logi%6E</c> is the sign-in endpoint to it, while
/// anything that reads the raw path (a limit by path, an allow-list) would see an unknown path. Refusing
/// every spelling but the canonical one makes the raw, decoded and routed path the same string for every
/// request that gets further.
/// <para>Canonical: segments of unreserved characters only (<c>A-Z a-z 0-9 - . _ ~</c>, RFC 3986 §2.3), no
/// empty, <c>.</c> or <c>..</c> segment, an optional trailing slash. The query string keeps its encoding.
/// It runs right after the request id and before CORS, authentication and routing.</para>
/// </summary>
public sealed partial class CanonicalPath(RequestDelegate next)
{
    public const string Code = "PATH_NOT_CANONICAL";

    // Atomic groups: no backtracking, so a long path of short segments costs linear time.
    [GeneratedRegex(@"^(?>/(?!\.\.?(?:/|$))[A-Za-z0-9._~-]+)*/?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Canonical();

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsCanonical(RawPath(context)))
        {
            await ApiErrors.WriteAsync(context, 400, Code, "The request path is not in its canonical form");
            return;
        }
        await next(context);
    }

    /// <summary>Whether the raw request path (as sent, before any decoding) is canonical.</summary>
    public static bool IsCanonical(string? rawPath)
    {
        if (string.IsNullOrEmpty(rawPath))
        {
            return false;
        }
        try
        {
            return Canonical().IsMatch(rawPath);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The request target as received, without its query string.</summary>
    public static string? RawPath(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw))
        {
            raw = context.Request.PathBase.Add(context.Request.Path).ToUriComponent();
        }
        var query = raw.IndexOf('?');
        return query < 0 ? raw : raw[..query];
    }
}
