using Microsoft.AspNetCore.Mvc.Filters;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Leads;

/// <summary>
/// The public demo-request form's front door, before the body is parsed (Java <c>DemoRequestGate</c> and
/// <c>LeadsWebConfig.GateRequired</c>). Every POST counts against the sender's address, valid or not, so a script cannot
/// probe validation for free; then the body is read once, up to <see cref="MaxBodyBytes"/>, and a larger one is refused
/// (413) before JSON sees it.
/// <para>It is a resource filter on the <c>requestDemo</c> action itself: MVC runs it after authorisation and before model
/// binding, for whatever spelling of the path routing matched. The Java gate matched the path and needed a second,
/// handler-side check (<c>GateRequired</c>) so that a spelling it missed failed closed; attached to the handler, the gate
/// cannot be missed, and the two Java pieces are one here. A non-canonical spelling never reaches routing anyway
/// (<see cref="CanonicalPath"/>, 400 <c>PATH_NOT_CANONICAL</c>).</para>
/// <para>The address is the one the platform's proxy reports (forwarded headers in the deployed profile): an
/// <c>X-Forwarded-For</c> the client sends itself is not trusted. The counters are in PostgreSQL
/// (<see cref="PostgresRateLimiter"/>), so a deploy or a second instance gives no fresh budget.</para>
/// </summary>
public sealed class DemoRequestGate(PostgresRateLimiter limiter, LeadMetrics metrics) : IAsyncResourceFilter
{
    public const int MaxBodyBytes = 4 * 1024;
    public const int PerAddressPerHour = 5;
    public const int PerAddressPerDay = 20;

    /// <summary>An IPv6 /48 holds 65,536 /64s: one budget for all of them, wider than one address's.</summary>
    public const int PerIpv6Network48PerHour = 20;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        var address = http.Connection.RemoteIpAddress;
        try
        {
            await limiter.AcquireAsync("demo-ip-hour", ClientInfo.LimitKeyOf(address), PerAddressPerHour, TimeSpan.FromHours(1), ct);
            await limiter.AcquireAsync("demo-ip-day", ClientInfo.LimitKeyOf(address), PerAddressPerDay, TimeSpan.FromDays(1), ct);
            if (ClientInfo.NetworkKeyOf(address) is { } network)
            {
                await limiter.AcquireAsync("demo-ip48-hour", network, PerIpv6Network48PerHour, TimeSpan.FromHours(1), ct);
            }
        }
        catch (RateLimitedException)
        {
            metrics.Count("rate_limited_address");
            throw;
        }
        if (http.Request.ContentLength > MaxBodyBytes)
        {
            throw TooLarge();
        }
        // Read from the network once and never past the cap, with or without a Content-Length to trust.
        var body = new byte[MaxBodyBytes + 1];
        var read = 0;
        int n;
        while (read < body.Length && (n = await http.Request.Body.ReadAsync(body.AsMemory(read), ct)) > 0)
        {
            read += n;
        }
        if (read > MaxBodyBytes)
        {
            throw TooLarge();
        }
        http.Request.Body = new MemoryStream(body, 0, read, writable: false);
        http.Request.ContentLength = read;
        await next();
    }

    private static ApiException TooLarge() =>
        new(413, "PAYLOAD_TOO_LARGE", "The request body is too large", null, new Dictionary<string, object> { ["maxBytes"] = MaxBodyBytes });
}

/// <summary>Puts <see cref="DemoRequestGate"/> on an action (resolved from the container, so it gets the shared limiter).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DemoRequestGateAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => true;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) => serviceProvider.GetRequiredService<DemoRequestGate>();
}
