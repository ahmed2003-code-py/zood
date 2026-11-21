using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zawed.Domain;

namespace Zawed.Api.Modules.Pricing;

/// <summary>
/// One price of a fuel, nationally or at one station, from an instant on (table <c>fuel_prices</c>, V4). Insert-only:
/// the runtime role has no UPDATE or DELETE on the table, so the price that applied to any past fueling can always be
/// recomputed. Not tenant-owned: every signed-in scope reads prices; only the platform scope inserts (RLS).
/// </summary>
public sealed class FuelPrice
{
    public Guid Id { get; set; }
    public FuelType FuelType { get; set; }

    /// <summary>Null: the national price. Otherwise an Ops override for one station (roadmap delta M1).</summary>
    public Guid? StationId { get; set; }

    public long PricePiastres { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

internal sealed class FuelPriceConfiguration : IEntityTypeConfiguration<FuelPrice>
{
    /// <summary>One price per fuel, place and instant (V4): a clash is <c>PRICE_EXISTS</c>.</summary>
    public const string OnePerInstantIndex = "fuel_prices_one_per_instant";

    public void Configure(EntityTypeBuilder<FuelPrice> builder)
    {
        builder.ToTable("fuel_prices");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(p => p.FuelType).HasColumnName("fuel_type");
        builder.Property(p => p.StationId).HasColumnName("station_id");
        builder.Property(p => p.PricePiastres).HasColumnName("price_piastres");
        builder.Property(p => p.EffectiveFrom).HasColumnName("effective_from");
        builder.Property(p => p.Note).HasColumnName("note");
        builder.Property(p => p.CreatedAt).HasColumnName("created_at");
        builder.Property(p => p.CreatedBy).HasColumnName("created_by");
    }
}
