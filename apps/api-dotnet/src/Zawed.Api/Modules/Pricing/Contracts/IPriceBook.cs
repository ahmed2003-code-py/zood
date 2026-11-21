using Zawed.Domain;

namespace Zawed.Api.Modules.Pricing.Contracts;

/// <summary>A price and where it came from. <see cref="StationId"/> is null for the national price.</summary>
public sealed record Price(FuelType FuelType, Guid? StationId, long PricePiastresPerLitre, DateTimeOffset EffectiveFrom);

/// <summary>
/// The price of a litre at a station at an instant, for the fueling engine and receipts (Java <c>pricing.PriceBook</c>).
/// Reads run in the caller's transaction; every signed-in scope may read prices.
/// </summary>
public interface IPriceBook
{
    /// <summary>
    /// The price in force: of the national price and the station's own override, the one that took effect last. A
    /// national price change therefore supersedes an older override.
    /// </summary>
    Task<Price?> PriceAtAsync(Guid stationId, FuelType fuelType, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>
    /// What a POS needs to apply <see cref="PriceAtAsync"/>'s rule itself until <paramref name="until"/>: the national
    /// price and the station's override in force at <paramref name="now"/>, and every change of either that takes
    /// effect after <paramref name="now"/> and no later than <paramref name="until"/>, oldest first.
    /// </summary>
    Task<IReadOnlyList<Price>> EntriesForAsync(Guid stationId, FuelType fuelType, DateTimeOffset now, DateTimeOffset until,
        CancellationToken ct = default);
}
