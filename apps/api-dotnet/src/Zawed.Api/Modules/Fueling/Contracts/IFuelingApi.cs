using Zawed.Domain;

namespace Zawed.Api.Modules.Fueling.Contracts;

/// <summary>
/// A fueling as other modules need it (tickets link one, station finance holds and reverses one). Read from the
/// immutable <c>fuelings</c> row; <see cref="Status"/> is <c>COMPLETED</c> or <c>DECLINED</c>, <see cref="Origin"/>
/// <c>ONLINE</c> or <c>OFFLINE</c>.
/// </summary>
public sealed record FuelingRef(Guid Id, string Reference, string Status, string Origin, Guid CompanyId, Guid VehicleId, Guid OperatorId,
    Guid StationId, Guid DeviceId, FuelType FuelType, long VolumeMl, long AmountPiastres, bool Billable, DateTimeOffset EvaluatedAt,
    Guid? LedgerTransactionId, bool PayableHeld);

/// <summary>
/// What each vehicle has used in its current periods, for the POS snapshot (B5 contract §2; Java <c>FuelingUsage.Usage</c>):
/// completed fuelings since each period start, in millilitres and piastres.
/// </summary>
public sealed record FuelingUsage(long DayMl, long WeekMl, long MonthMl, long DayPiastres, long WeekPiastres, long MonthPiastres)
{
    public static readonly FuelingUsage None = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// What other modules may ask of the fueling module (wave 3A). Every call runs in the caller's transaction and scope
/// (<c>IDbSession.Current</c>), so row-level security decides what is found: a company sees its own fuelings, an
/// operator those at its stations, the platform all of them.
/// </summary>
public interface IFuelingApi
{
    /// <summary>
    /// Whether the caller can see this fueling (Java <c>Tickets.requireVisible("fuelings", ...)</c>:
    /// <c>select exists (select 1 from fuelings where id = ?)</c>).
    /// </summary>
    Task<bool> VisibleAsync(Guid fuelingId, CancellationToken ct = default);

    /// <summary>The fueling, or null when it does not exist or the caller cannot see it.</summary>
    Task<FuelingRef?> FindAsync(Guid fuelingId, CancellationToken ct = default);

    /// <summary>
    /// Every vehicle with a completed fueling since the earliest of the three starts, with what it used since each
    /// (Java <c>FuelingUsage.usageSince</c>). Read without locks: the device uses it as a starting point and the engine
    /// decides again at sync.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, FuelingUsage>> UsageSinceAsync(DateTimeOffset dayStart, DateTimeOffset weekStart, DateTimeOffset monthStart,
        CancellationToken ct = default);
}
