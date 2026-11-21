using Zawed.Domain;
using Zawed.Domain.Rules;

namespace Zawed.Api.Modules.Fleet.Contracts;

/// <summary>A tag, its vehicle and its company, as read under the tag's row lock (Java <c>TagGate.TagAtPump</c>).</summary>
public sealed record TagAtPump(Guid TagId, Guid VehicleId, Guid CompanyId, bool TagActive, DateTimeOffset? CooldownUntil, string Plate,
    FuelType VehicleFuel, bool VehicleActive, bool Vip, long TankCapacityMl, long? VipMonthlyCeilingMl);

/// <summary>An active limit rule of a vehicle, in its own unit: millilitres or piastres (Java <c>TagGate.LimitRule</c>).</summary>
public sealed record TagLimit(FuelingRules.LimitKind Kind, Periods.Period Period, long MaxValue);

/// <summary>The tag and company a scanned UID names (Java <c>TagGate.Bound</c>).</summary>
public sealed record BoundTag(Guid TagId, Guid CompanyId);

/// <summary>
/// What the fueling engine asks of the fleet at the pump (B4 contract §3; Java <c>TagGate</c>). Every call runs in the
/// engine's transaction and scope (<c>IDbSession.Current</c>). Lock order is the company wallet, then the vehicle, then the
/// tag: <see cref="LockAsync"/> is called only once the engine holds the wallet.
/// </summary>
public interface ITagGate
{
    /// <summary>The tag and company a scanned UID names, without locking. Null for a UID never bound, or not a UID at all.</summary>
    Task<BoundTag?> FindAsync(string? rawUid, CancellationToken ct = default);

    /// <summary>Locks the vehicle (shared), then the tag row, and reads the tag with its vehicle. 404 <c>Tag</c> when unknown.</summary>
    Task<TagAtPump> LockAsync(Guid tagId, CancellationToken ct = default);

    Task<IReadOnlyList<TagLimit>> ActiveLimitsAsync(Guid vehicleId, CancellationToken ct = default);

    /// <summary>Starts the cooling-off period after an approved fueling (PRD §6.4), mirrored from the tag write.</summary>
    Task StartCooldownAsync(Guid tagId, DateTimeOffset until, CancellationToken ct = default);
}
