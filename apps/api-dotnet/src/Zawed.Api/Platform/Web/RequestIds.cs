using System.Text.RegularExpressions;

namespace Zawed.Api.Platform.Web;

/// <summary>
/// Gives every request an id, puts it on every log line written while the request is handled (a log
/// scope), and returns it as <c>X-Request-Id</c> (Java <c>RequestIdFilter</c>). It runs first, so a
/// request that is refused early still has one. An inbound id is kept only when it is short and plain,
/// because it ends up in log lines, and an unchecked header is how a forged line gets into a log.
/// </summary>
public sealed partial class RequestIds(RequestDelegate next, ILogger<RequestIds> logger)
{
    public const string Header = "X-Request-Id";
    private const string ItemKey = "zawed.requestId";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SafeId();

    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[Header].ToString();
        var id = inbound.Length > 0 && SafeId().IsMatch(inbound) ? inbound : Guid.NewGuid().ToString();
        context.Items[ItemKey] = id;
        context.TraceIdentifier = id;
        context.Response.Headers[Header] = id;
        using (logger.BeginScope(new Dictionary<string, object> { ["requestId"] = id }))
        {
            await next(context);
        }
    }

    /// <summary>The id of the request being handled, or null outside a request.</summary>
    public static string? Current(HttpContext? context) =>
        context?.Items.TryGetValue(ItemKey, out var id) == true ? id as string : null;
}
