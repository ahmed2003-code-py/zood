namespace Zawed.Api.Modules.Network.Contracts;

/// <summary>A station operator as other modules need it.</summary>
public sealed record OperatorRef(Guid Id, string NameAr, string NameEn, bool Active);

/// <summary>
/// What other modules may ask about station operators (Java <c>network.OperatorDirectory</c>). Reads run in the caller's
/// transaction and scope, so an operator or a station outside it is simply not found.
/// </summary>
public interface IOperatorDirectory
{
    Task<OperatorRef?> FindAsync(Guid operatorId, CancellationToken ct = default);

    /// <summary>True if the station exists (whatever its status).</summary>
    Task<bool> StationExistsAsync(Guid stationId, CancellationToken ct = default);
}
