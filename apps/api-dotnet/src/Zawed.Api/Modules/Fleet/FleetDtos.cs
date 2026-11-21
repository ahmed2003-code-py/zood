using Zawed.Api.Platform.Web;
using Zawed.Domain;

namespace Zawed.Api.Modules.Fleet;

// The request and response records of the fleet endpoints (Java FleetApi), named and shaped as the Java records, so the
// OpenAPI document and the JSON are the same.

public sealed record VehicleBody(
    Guid? DepartmentId,
    [NotBlank, Size(max: 20)] string Plate,
    [NotBlank, Size(max: 60)] string Make,
    [NotBlank, Size(max: 60)] string Model,
    [Min(1950), Max(2100)] int? Year,
    [NotNull] FuelType? FuelType,
    [NotNull, Positive, Max(Vehicles.MaxTankMl)] long? TankCapacityMl,
    [NotNull] Tier? Tier,
    [Positive] long? VipMonthlyCeilingMl,
    [Size(max: 40)] string? FleetCode)
{
    internal Vehicles.Draft Draft() => new(DepartmentId, Plate, Make, Model, Year, FuelType!.Value, TankCapacityMl!.Value, Tier!.Value,
        VipMonthlyCeilingMl, FleetCode);
}

public sealed record CreateVehicleRequest([NotNull] Guid? CompanyId, [NotNull] VehicleBody? Vehicle);

public sealed record VehicleStatusRequest([NotNull] VehicleStatus? Status, [NotBlank, Size(max: 500)] string Reason);

public sealed record LimitRequest([NotNull] LimitKind? Kind, [NotNull] LimitPeriod? Period, [NotNull, Positive] long? MaxValue);

public sealed record IssueTagRequest([NotBlank, Size(max: 64)] string Uid);

public sealed record TagStatusRequest([NotNull] TagStatus? Status, [NotBlank, Size(max: 500)] string Reason);

public sealed record VehicleResponse(
    Guid Id,
    Guid CompanyId,
    Guid? DepartmentId,
    string Plate,
    string Make,
    string Model,
    int? Year,
    FuelType FuelType,
    long TankCapacityMl,
    Tier Tier,
    long? VipMonthlyCeilingMl,
    VehicleStatus Status,
    DateTimeOffset CreatedAt,
    string? FleetCode)
{
    public static VehicleResponse Of(Vehicle v) => new(v.Id, v.CompanyId, v.DepartmentId, v.Plate, v.Make, v.Model, v.Year, v.FuelType,
        v.TankCapacityMl, v.Tier, v.VipMonthlyCeilingMl, v.Status, v.CreatedAt, v.FleetCode);

    /// <summary>
    /// What the company sees: no VIP ceiling. The safeguard is invisible to the company by design (PRD §4.5): it only
    /// raises a flag for Operations.
    /// </summary>
    public static VehicleResponse ForCompany(Vehicle v) => Of(v) with { VipMonthlyCeilingMl = null };
}

/// <summary>Only the last four characters of the UID are ever returned.</summary>
public sealed record TagResponse(
    Guid Id,
    Guid VehicleId,
    string UidLast4,
    TagStatus Status,
    string? StatusReason,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset CreatedAt)
{
    public static TagResponse Of(RfidTag t) => new(t.Id, t.VehicleId, t.UidLast4, t.Status, t.StatusReason, t.CooldownUntil, t.CreatedAt);
}

public sealed record LimitResponse(Guid Id, LimitKind Kind, LimitPeriod Period, long MaxValue, DateTimeOffset CreatedAt)
{
    public static LimitResponse Of(LimitRule r) => new(r.Id, r.Kind, r.Period, r.MaxValue, r.CreatedAt);
}

/// <summary>A vehicle with its tags and active limits: the detail screen in one call.</summary>
public sealed record VehicleDetail(VehicleResponse Vehicle, IReadOnlyList<TagResponse> Tags, IReadOnlyList<LimitResponse> Limits);
