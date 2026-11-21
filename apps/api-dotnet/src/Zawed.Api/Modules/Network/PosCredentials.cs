using System.Globalization;
using System.Security.Claims;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Network;

/// <summary>
/// The POS chain's check (B4 contract §1; Java <c>network.PosCredentials</c>): <c>Authorization: Bearer &lt;device token&gt;</c>
/// names an ACTIVE device, and <c>X-Agent-Session</c>, when sent, names a live session of an ACTIVE agent of that device's
/// station opened on that same device. Before both, a terminal older than the minimum POS contract is told to update.
/// </summary>
public sealed class PosCredentials(DeviceChannel devices, AgentSessions agents, IRateLimiter limiter) : IPosAuthenticator
{
    public const string AgentHeader = "X-Agent-Session";

    /// <summary>A busy pump does a fueling every minute or two; this is far above that and stops a runaway client.</summary>
    public const int RequestsPerMinute = 120;

    public async Task<ClaimsPrincipal> AuthenticateAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        RequireContract(context.Request);
        var header = First(context.Request.Headers.Authorization);
        var token = header is not null && header.Length >= 7 && header[..7].Equals("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? Rows.JavaStrip(header[7..]) : null;
        var device = (token is null ? null : await devices.VerifyAsync(token, ct))
            ?? throw new ApiException(401, "DEVICE_UNAUTHENTICATED", "The device could not be authenticated");
        limiter.Acquire("pos:" + device.DeviceId, RequestsPerMinute, TimeSpan.FromMinutes(1));
        var session = First(context.Request.Headers[AgentHeader]);
        AgentRef? agent = null;
        if (!Rows.JavaBlank(session))
        {
            var presented = Rows.JavaStrip(session!);
            agent = await agents.VerifyAsync(device, presented, ct)
                ?? throw new ApiException(401, "AGENT_SESSION_INVALID", "The agent session has ended; sign in again");
            await agents.TouchAsync(presented, ct);
        }
        await devices.TouchAsync(device.DeviceId, ct);
        return new PosPrincipal(device.DeviceId, device.Code, device.StationId, device.OperatorId, agent?.AgentId, agent?.WorkerId,
            agent?.FullName, agent is { MustChangePin: true }).ToClaimsPrincipal();
    }

    /// <summary>
    /// B5 contract §1: a terminal older than the minimum protocol is told to update before anything else. A request without
    /// the header is the current web simulator. The number is read as Java's <c>Integer.parseInt</c> reads it; anything
    /// else counts as version 0.
    /// </summary>
    public static void RequireContract(HttpRequest request)
    {
        var header = First(request.Headers[ApiContract.PosContractHeader]);
        if (Rows.JavaBlank(header))
        {
            return;
        }
        if (JavaParseInt(Rows.JavaStrip(header!)) < ApiContract.PosContractMinimum)
        {
            throw new ApiException(426, "CONTRACT_UPGRADE_REQUIRED", "This terminal must be updated before it can work", null,
                new Dictionary<string, object> { ["minimum"] = ApiContract.PosContractMinimum });
        }
    }

    public static DeviceRef DeviceOf(PosPrincipal principal) =>
        new(principal.DeviceId, principal.DeviceCode, principal.StationId, principal.OperatorId);

    public static AgentRef AgentOf(PosPrincipal principal) =>
        new(principal.AgentId!.Value, principal.AgentWorkerId!, principal.AgentName!, principal.MustChangePin);

    /// <summary>The first value of a header, as the servlet <c>getHeader</c> returns it.</summary>
    private static string? First(Microsoft.Extensions.Primitives.StringValues values) => values.Count > 0 ? values[0] : null;

    /// <summary><c>Integer.parseInt</c>: an optional sign and decimal digits of any script; 0 where it would throw.</summary>
    private static int JavaParseInt(string text)
    {
        var negative = text.StartsWith('-');
        var digits = text.Length > 0 && text[0] is '+' or '-' ? text[1..] : text;
        if (digits.Length == 0)
        {
            return 0;
        }
        long value = 0;
        foreach (var c in digits)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.DecimalDigitNumber)
            {
                return 0;
            }
            value = value * 10 + (long)CharUnicodeInfo.GetDecimalDigitValue(c);
            if (value > (long)int.MaxValue + 1)
            {
                return 0;
            }
        }
        value = negative ? -value : value;
        return value is < int.MinValue or > int.MaxValue ? 0 : (int)value;
    }
}
