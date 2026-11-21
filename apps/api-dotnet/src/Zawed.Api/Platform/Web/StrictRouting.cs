using Microsoft.AspNetCore.Routing.Patterns;

namespace Zawed.Api.Platform.Web;

/// <summary>
/// Routes match as Spring's did (B7 "path matching", ADR 0012 amendment): case-sensitive and without the trailing
/// slash. ASP.NET Core routing would also send <c>/api/v1/auth/LOGIN</c> and <c>/api/v1/auth/login/</c> to the sign-in
/// step, where the Java API answered them as unknown paths (401 to an anonymous caller, 404 to a signed-in one); a
/// limit or an allow-list keyed by the canonical path would then miss them. After routing, an endpoint whose literal
/// segments do not equal the request's exactly is dropped, so the request is answered as no route at all.
/// </summary>
public static class StrictRouting
{
    public static IApplicationBuilder UseStrictRouting(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.GetEndpoint() is RouteEndpoint endpoint && !Matches(endpoint.RoutePattern, context.Request.Path.Value ?? ""))
        {
            context.SetEndpoint(null);
            context.Request.RouteValues.Clear();
        }
        await next(context);
    });

    /// <summary>Whether <paramref name="path"/> spells every literal segment of the pattern exactly, with no extra slash.</summary>
    public static bool Matches(RoutePattern pattern, string path)
    {
        if (pattern.PathSegments.Any(s => s.Parts.Any(p => p is RoutePatternParameterPart { IsCatchAll: true })))
        {
            return true;
        }
        var segments = path.Split('/');
        // "/a/b" splits into "", "a", "b"; a trailing slash leaves one more, empty, segment.
        if (segments.Length != pattern.PathSegments.Count + 1 || segments[0].Length != 0)
        {
            return false;
        }
        for (var i = 0; i < pattern.PathSegments.Count; i++)
        {
            var parts = pattern.PathSegments[i].Parts;
            if (parts.Count == 1 && parts[0] is RoutePatternLiteralPart literal
                && !string.Equals(literal.Content, segments[i + 1], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
