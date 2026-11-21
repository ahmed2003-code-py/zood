using System.Globalization;
using System.Security.Claims;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Network.Contracts;

/// <summary>
/// Who is behind a POS request (Java <c>network.PosPrincipal</c>): an ACTIVE device at one station, and the agent signed
/// in on it (null on the agent sign-in call itself). Both are read from the database on every request, so a revoked
/// device or a suspended agent stops at once (B4 contract §1). Carried as claims on <see cref="HttpContext.User"/>, with
/// the chain's authorities (<see cref="PosChannel"/>).
/// </summary>
public sealed record PosPrincipal(Guid DeviceId, string DeviceCode, Guid StationId, Guid OperatorId, Guid? AgentId,
    string? AgentWorkerId, string? AgentName, bool MustChangePin)
{
    public const string AuthenticationType = "zawed-pos";
    private const string DeviceClaim = "zawed:pos-device";
    private const string DeviceCodeClaim = "zawed:pos-device-code";
    private const string StationClaim = "zawed:pos-station";
    private const string OperatorClaim = "zawed:pos-operator";
    private const string AgentClaim = "zawed:pos-agent";
    private const string WorkerClaim = "zawed:pos-worker";
    private const string AgentNameClaim = "zawed:pos-agent-name";
    private const string MustChangePinClaim = "zawed:pos-must-change-pin";

    public bool HasAgent => AgentId is not null;

    /// <summary>
    /// The device alone, without whoever is signed in on it now. Offline sync (B5) works with this: a queued capture
    /// names its own agent, checked against the session history.
    /// </summary>
    public PosPrincipal DeviceOnly() => HasAgent ? new(DeviceId, DeviceCode, StationId, OperatorId, null, null, null, false) : this;

    public ClaimsPrincipal ToClaimsPrincipal()
    {
        var claims = new List<Claim>
        {
            new(DeviceClaim, DeviceId.ToString()),
            new(DeviceCodeClaim, DeviceCode),
            new(StationClaim, StationId.ToString()),
            new(OperatorClaim, OperatorId.ToString()),
            new(MustChangePinClaim, MustChangePin ? "true" : "false"),
            new(PosChannel.AuthorityClaim, PosChannel.Device),
        };
        if (AgentId is { } agent)
        {
            claims.Add(new(AgentClaim, agent.ToString()));
            claims.Add(new(WorkerClaim, AgentWorkerId ?? ""));
            claims.Add(new(AgentNameClaim, AgentName ?? ""));
            claims.Add(new(PosChannel.AuthorityClaim, MustChangePin ? PosChannel.AgentPinChange : PosChannel.Agent));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));
    }

    /// <summary>The principal of a POS request, or null when the caller is not a verified device.</summary>
    public static PosPrincipal? From(ClaimsPrincipal? user)
    {
        var identity = user?.Identities.FirstOrDefault(i => i.AuthenticationType == AuthenticationType && i.IsAuthenticated);
        if (identity is null)
        {
            return null;
        }
        string Value(string type) => identity.FindFirst(type)!.Value;
        var agent = identity.FindFirst(AgentClaim) is { } a ? Guid.Parse(a.Value, CultureInfo.InvariantCulture) : (Guid?)null;
        return new PosPrincipal(Guid.Parse(Value(DeviceClaim), CultureInfo.InvariantCulture), Value(DeviceCodeClaim),
            Guid.Parse(Value(StationClaim), CultureInfo.InvariantCulture), Guid.Parse(Value(OperatorClaim), CultureInfo.InvariantCulture),
            agent, agent is null ? null : Value(WorkerClaim), agent is null ? null : Value(AgentNameClaim), Value(MustChangePinClaim) == "true");
    }
}
