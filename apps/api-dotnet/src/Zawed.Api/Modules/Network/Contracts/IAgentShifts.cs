using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Modules.Network.Contracts;

/// <summary>The agent as the fueling row records them.</summary>
public sealed record AgentOnDevice(Guid AgentId, string WorkerId);

/// <summary>
/// Who was signed in on a device at a past instant (B5 contract §3; Java <c>network.AgentShifts</c>). An offline capture
/// names its agent in the request body, and a request body is never trusted for who did something: the agent counts only
/// when the server's own record shows a session of that agent on that device covering the capture time.
/// <para>Both read the session history in <paramref name="tx"/>, which must run under the platform scope (agent sessions
/// are platform-only under row-level security); in any other scope they find nothing and the capture is refused.</para>
/// </summary>
public interface IAgentShifts
{
    /// <summary>
    /// The agent, if they belong to <paramref name="stationId"/> and had a session on <paramref name="deviceId"/> open at
    /// <paramref name="at"/> (within the device clock tolerance): started by then, and not signed out, revoked or expired before it.
    /// </summary>
    Task<AgentOnDevice?> OnDeviceAtAsync(DbTx tx, Guid deviceId, Guid stationId, Guid agentId, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>
    /// The agent, if they belong to <paramref name="stationId"/>, whatever their sessions. A capture whose agent is of the
    /// station but whose session cannot be confirmed is still recorded, flagged; an agent of another station is not.
    /// </summary>
    Task<AgentOnDevice?> OfStationAsync(DbTx tx, Guid stationId, Guid agentId, CancellationToken ct = default);
}
