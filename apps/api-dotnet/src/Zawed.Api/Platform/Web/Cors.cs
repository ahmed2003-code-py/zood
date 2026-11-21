using Microsoft.Extensions.Options;
using Zawed.Api.Platform.Config;

namespace Zawed.Api.Platform.Web;

/// <summary>
/// CORS as the Java API answers it (Spring's <c>DefaultCorsProcessor</c> with the <c>SecurityConfig</c>
/// configuration), rather than ASP.NET Core's middleware, whose answers differ in ways a test can see:
/// <list type="bullet">
/// <item>a preflight from an allowed origin with an allowed method and headers answers <c>200</c> with the
/// allow headers, credentials and a one-hour max age;</item>
/// <item>any cross-origin request (preflight or actual) from an origin not listed answers <c>403</c> with
/// no CORS headers, before it reaches authentication or a controller;</item>
/// <item><c>Vary: Origin, Access-Control-Request-Method, Access-Control-Request-Headers</c> on every response.</item>
/// </list>
/// Credentials are allowed, so the origin list is always explicit (<see cref="ZawedOptionsValidator"/>).
/// </summary>
public sealed class Cors(RequestDelegate next, IOptions<ZawedOptions> options)
{
    public static readonly string[] AllowedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];

    public static readonly string[] AllowedHeaders =
    [
        "Authorization", "Content-Type", "Accept", "Idempotency-Key", RequestIds.Header, "X-Zawed-Client",
        "X-Agent-Session", ApiContract.PosContractHeader,
    ];

    public static readonly string[] ExposedHeaders = [RequestIds.Header, "Retry-After"];

    private readonly HashSet<string> origins = options.Value.Cors.Origins().ToHashSet(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        var response = context.Response;
        AddVary(response);

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length == 0 || IsSameOrigin(context, origin))
        {
            await next(context);
            return;
        }
        var preflight = HttpMethods.IsOptions(context.Request.Method)
            && context.Request.Headers.ContainsKey("Access-Control-Request-Method");
        if (!origins.Contains(origin))
        {
            await Reject(context);
            return;
        }
        if (preflight)
        {
            var method = context.Request.Headers["Access-Control-Request-Method"].ToString();
            var requested = context.Request.Headers["Access-Control-Request-Headers"].ToString()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!AllowedMethods.Contains(method, StringComparer.OrdinalIgnoreCase)
                || requested.Any(h => !AllowedHeaders.Contains(h, StringComparer.OrdinalIgnoreCase)))
            {
                await Reject(context);
                return;
            }
            response.Headers.AccessControlAllowOrigin = origin;
            response.Headers.AccessControlAllowMethods = string.Join(",", AllowedMethods);
            if (requested.Length > 0)
            {
                response.Headers.AccessControlAllowHeaders = string.Join(", ", requested);
            }
            response.Headers.AccessControlAllowCredentials = "true";
            response.Headers.AccessControlMaxAge = "3600";
            response.StatusCode = 200;
            return;
        }
        // On starting, so an error answer (whose headers the exception handler clears) keeps them too.
        response.OnStarting(() =>
        {
            AddVary(response);
            response.Headers.AccessControlAllowOrigin = origin;
            response.Headers.AccessControlAllowCredentials = "true";
            response.Headers.AccessControlExposeHeaders = string.Join(", ", ExposedHeaders);
            return Task.CompletedTask;
        });
        await next(context);
    }

    private static void AddVary(HttpResponse response)
    {
        if (!response.Headers.Vary.Contains("Origin"))
        {
            response.Headers.Append("Vary", "Origin");
            response.Headers.Append("Vary", "Access-Control-Request-Method");
            response.Headers.Append("Vary", "Access-Control-Request-Headers");
        }
    }

    /// <summary>Spring answers a refused CORS request with 403 and the text "Invalid CORS request".</summary>
    private static async Task Reject(HttpContext context)
    {
        context.Response.StatusCode = 403;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("Invalid CORS request");
    }

    private static bool IsSameOrigin(HttpContext context, string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
}
