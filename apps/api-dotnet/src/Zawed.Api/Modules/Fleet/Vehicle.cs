using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zawed.Api.Platform.Persistence;
using Zawed.Domain;

namespace Zawed.Api.Modules.Fleet;

public enum Tier
{
    NORMAL,
    VIP,
}

public enum VehicleStatus
{
    ACTIVE,
    SUSPENDED,
    DECOMMISSIONED,
}

public enum LimitKind
{
    /// <summary>Millilitres per period.</summary>
    VOLUME,

    /// <summary>Piastres per period.</summary>
    AMOUNT,
}

public enum LimitPeriod
{
    DAY,
    WEEK,
    MONTH,
}

public enum TagStatus
{
    ACTIVE,
    LOST,
    STOLEN,
    DEACTIVATED,
    REPLACEMENT_PENDING,
}

/// <summary>
/// A vehicle (table <c>vehicles</c>, V4 and V14; Java <c>Vehicles.Vehicle</c>). The company is fixed at creation
/// (<c>vehicles_identity_guard</c>), and the database refuses a department, tag or limit of another company.
/// </summary>
public sealed class Vehicle : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>Stored and shown exactly as registered (approved decision: never transliterated).</summary>
    public string Plate { get; set; } = "";

    /// <summary>The comparison form (<see cref="PlateKey"/>): only finds duplicates.</summary>
    public string PlateKey { get; set; } = "";

    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public int? Year { get; set; }
    public FuelType FuelType { get; set; }
    public long TankCapacityMl { get; set; }
    public Tier Tier { get; set; }

    /// <summary>Invisible VIP safeguard (PRD §4.5): flags, never blocks. VIP only.</summary>
    public long? VipMonthlyCeilingMl { get; set; }

    public string? FleetCode { get; set; }
    public VehicleStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

internal sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    /// <summary>A fleet code is unique per company among vehicles in service (V14).</summary>
    public const string FleetCodeIndex = "vehicles_fleet_code_key";

    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.ToTable("vehicles");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(v => v.CompanyId).HasColumnName("company_id");
        builder.Property(v => v.DepartmentId).HasColumnName("department_id");
        builder.Property(v => v.Plate).HasColumnName("plate");
        builder.Property(v => v.PlateKey).HasColumnName("plate_key");
        builder.Property(v => v.Make).HasColumnName("make");
        builder.Property(v => v.Model).HasColumnName("model");
        builder.Property(v => v.Year).HasColumnName("year");
        builder.Property(v => v.FuelType).HasColumnName("fuel_type");
        builder.Property(v => v.TankCapacityMl).HasColumnName("tank_capacity_ml");
        builder.Property(v => v.Tier).HasColumnName("tier");
        builder.Property(v => v.VipMonthlyCeilingMl).HasColumnName("vip_monthly_ceiling_ml");
        builder.Property(v => v.FleetCode).HasColumnName("fleet_code");
        builder.Property(v => v.Status).HasColumnName("status");
        builder.Property(v => v.CreatedAt).HasColumnName("created_at");
        builder.Property(v => v.CreatedBy).HasColumnName("created_by");
        builder.Property(v => v.UpdatedAt).HasColumnName("updated_at");
        builder.Property(v => v.UpdatedBy).HasColumnName("updated_by");
    }
}

/// <summary>
/// An RFID tag (table <c>rfid_tags</c>, V4; Java <c>Tags.Tag</c>). The UID is never stored in clear: it is kept
/// encrypted (<see cref="UidEncrypted"/>, bound to the tag id by <see cref="Tags.AssociatedData"/>) and as a keyed hash
/// that finds it (<see cref="UidLookup"/>). Responses show the last four characters only.
/// </summary>
public sealed class RfidTag : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VehicleId { get; set; }
    public byte[] UidEncrypted { get; set; } = [];
    public string UidLookup { get; set; } = "";
    public string UidLast4 { get; set; } = "";
    public TagStatus Status { get; set; }
    public string? StatusReason { get; set; }

    /// <summary>Mirror of the cooldown record written on the tag itself (PRD §7.3).</summary>
    public DateTimeOffset? CooldownUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

internal sealed class RfidTagConfiguration : IEntityTypeConfiguration<RfidTag>
{
    public void Configure(EntityTypeBuilder<RfidTag> builder)
    {
        builder.ToTable("rfid_tags");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.CompanyId).HasColumnName("company_id");
        builder.Property(t => t.VehicleId).HasColumnName("vehicle_id");
        builder.Property(t => t.UidEncrypted).HasColumnName("uid_encrypted");
        builder.Property(t => t.UidLookup).HasColumnName("uid_lookup");
        builder.Property(t => t.UidLast4).HasColumnName("uid_last4");
        builder.Property(t => t.Status).HasColumnName("status");
        builder.Property(t => t.StatusReason).HasColumnName("status_reason");
        builder.Property(t => t.CooldownUntil).HasColumnName("cooldown_until");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.CreatedBy).HasColumnName("created_by");
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at");
        builder.Property(t => t.UpdatedBy).HasColumnName("updated_by");
    }
}

/// <summary>
/// A limit rule of a vehicle (table <c>limit_rules</c>, V4; Java <c>Vehicles.LimitRule</c>): one active rule per kind
/// and period; a change retires the old rule and adds a new one, so what applied when stays readable.
/// </summary>
public sealed class LimitRule : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VehicleId { get; set; }
    public LimitKind Kind { get; set; }
    public LimitPeriod Period { get; set; }

    /// <summary>Millilitres for <see cref="LimitKind.VOLUME"/>, piastres for <see cref="LimitKind.AMOUNT"/>.</summary>
    public long MaxValue { get; set; }

    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public Guid? DeactivatedBy { get; set; }
}

internal sealed class LimitRuleConfiguration : IEntityTypeConfiguration<LimitRule>
{
    public void Configure(EntityTypeBuilder<LimitRule> builder)
    {
        builder.ToTable("limit_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.CompanyId).HasColumnName("company_id");
        builder.Property(r => r.VehicleId).HasColumnName("vehicle_id");
        builder.Property(r => r.Kind).HasColumnName("kind");
        builder.Property(r => r.Period).HasColumnName("period");
        builder.Property(r => r.MaxValue).HasColumnName("max_value");
        builder.Property(r => r.Active).HasColumnName("active");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.Property(r => r.CreatedBy).HasColumnName("created_by");
        builder.Property(r => r.DeactivatedAt).HasColumnName("deactivated_at");
        builder.Property(r => r.DeactivatedBy).HasColumnName("deactivated_by");
    }
}
