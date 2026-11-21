using Zawed.Api.Platform.Web;
using Zawed.Domain;

namespace Zawed.Api.Modules.Pricing;

/// <param name="StationId">Null for the national price.</param>
/// <param name="PricePiastres">Piastres per litre.</param>
/// <param name="EffectiveFrom">Defaults to now; never in the past.</param>
public sealed record ScheduleRequest(
    [NotNull] FuelType? FuelType,
    Guid? StationId,
    [NotNull, Positive, Max(100_000)] long? PricePiastres,
    DateTimeOffset? EffectiveFrom,
    [Size(max: 500)] string? Note);

public sealed record PriceResponse(FuelType FuelType, Guid? StationId, long PricePiastresPerLitre, DateTimeOffset EffectiveFrom);

public sealed record PriceEntryResponse(Guid Id, FuelType FuelType, Guid? StationId, long PricePiastresPerLitre, DateTimeOffset EffectiveFrom,
    string? Note, DateTimeOffset CreatedAt);
