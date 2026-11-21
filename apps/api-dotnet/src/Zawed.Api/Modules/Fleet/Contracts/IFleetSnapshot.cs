using Zawed.Domain;
using Zawed.Domain.Rules;

namespace Zawed.Api.Modules.Fleet.Contracts;

/// <param name="Digest">
/// The first <see cref="IFleetSnapshot.DigestHexLength"/> hex characters (32 bits) of the tag's HMAC under the device key:
/// enough to recognise a tag among the fleet, too short to recover its UID (a brute force finds millions of candidates per
/// tag, and the server decides at sync).
/// </param>
public sealed record SnapshotTag(string Digest, Guid VehicleId, string Status, DateTimeOffset? CooldownUntil);

public sealed record SnapshotVehicle(Guid Id, Guid CompanyId, string Plate, FuelType FuelType, bool Vip, long TankCapacityMl, bool Active);

public sealed record SnapshotLimit(Guid VehicleId, FuelingRules.LimitKind Kind, Periods.Period Period, long MaxValue);

public sealed record SnapshotFleet(IReadOnlyList<SnapshotTag> Tags, IReadOnlyList<SnapshotVehicle> Vehicles, IReadOnlyList<SnapshotLimit> Limits);

/// <summary>
/// The fleet as a POS needs it offline (B5 contract §2; Java <c>FleetSnapshot</c>): every tag that can fuel, its vehicle
/// and the vehicle's limits. A tag is identified by a digest of its UID under a key given to one device, never by the UID
/// itself. Runs in the caller's transaction and scope.
/// </summary>
public interface IFleetSnapshot
{
    /// <summary>32 bits of the HMAC.</summary>
    const int DigestHexLength = 8;

    /// <summary>
    /// Tags whose UID is this short or shorter (4-byte UIDs, 2^32 values) are left out of the snapshot: even a truncated
    /// digest would identify them. The terminal decides them online only.
    /// </summary>
    const int ShortestUidInSnapshot = 9;

    /// <param name="digestKey">the device's key: each tag's digest is HMAC-SHA256(digestKey, normalised UID) in lower-case hex</param>
    Task<SnapshotFleet> SnapshotAsync(byte[] digestKey, CancellationToken ct = default);
}
