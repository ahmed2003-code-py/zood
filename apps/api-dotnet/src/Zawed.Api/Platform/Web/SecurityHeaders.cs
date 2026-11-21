namespace Zawed.Api.Platform.Web;

/// <summary>
/// The response headers Spring Security writes by default, which the Java API sends on every response
/// (checked against zawed-dev on 2026-11-21): no caching unless the endpoint set its own cache headers,
/// <c>nosniff</c>, <c>X-Frame-Options: DENY</c>, <c>X-XSS-Protection: 0</c>, and HSTS on HTTPS requests.
/// </summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            if (!headers.ContainsKey("Cache-Control") && !headers.ContainsKey("Expires") && !headers.ContainsKey("Pragma"))
            {
                headers.CacheControl = "no-cache, no-store, max-age=0, must-revalidate";
                headers.Pragma = "no-cache";
                headers.Expires = "0";
            }
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.XXSSProtection = "0";
            if (context.Request.IsHttps)
            {
                headers.StrictTransportSecurity = "max-age=31536000 ; includeSubDomains";
            }
            return Task.CompletedTask;
        });
        return next(context);
    }
}
