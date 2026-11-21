using Zawed.Domain;

namespace Zawed.Api.Modules.Network.Contracts;

/// <summary>
/// A station as the fueling engine and receipts need it (Java <c>StationDirectory.StationRef</c>). Rules are evaluated in
/// <see cref="Zone"/> (<see cref="TimeZone"/> is its IANA id). <see cref="Active"/>: the station and its operator are both
/// ACTIVE; suspending an operator stops its stations.
/// </summary>
public sealed record StationRef(Guid Id, Guid OperatorId, string NameAr, string NameEn, string TimeZone, TimeZoneInfo Zone,
    IReadOnlyList<FuelType> FuelTypes, bool Active);

/// <summary>What other modules may ask about a station (Java <c>network.StationDirectory</c>). Reads run in the caller's transaction and scope.</summary>
public interface IStationDirectory
{
    Task<StationRef?> FindStationAsync(Guid stationId, CancellationToken ct = default);
}
