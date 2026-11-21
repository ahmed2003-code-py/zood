using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zawed.Api.Modules.Tenancy;

public enum CompanyStatus
{
    ACTIVE,
    FROZEN,
}

/// <summary>
/// A customer company (table <c>companies</c>, V4). The row is the tenant itself, so it is not
/// <c>ICompanyOwned</c>: row-level security (<c>companies_by_scope</c>) shows a company scope its own row only, and
/// every read here is by id or on the Ops (platform) side.
/// </summary>
public sealed class Company
{
    public Guid Id { get; set; }
    public string LegalName { get; set; } = "";
    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string? TaxId { get; set; }
    public string? CommercialRegistration { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
    public CompanyStatus Status { get; set; }

    /// <summary>Minutes between two fuelings of the same tag (PRD 6.4: 120 to 240, a CHECK in the table).</summary>
    public int CooldownMinutes { get; set; }

    public long LowBalanceThresholdPiastres { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>
    /// The database's <c>tax_id_key(text)</c> (V23): digits in ASCII, no separators. The unique index and Ops search
    /// compare tax ids in this form. Only usable inside a LINQ query, where EF translates it.
    /// </summary>
    public static string? TaxIdKey(string? taxId) => throw new InvalidOperationException("tax_id_key runs in the database only");
}

/// <summary>Maps <see cref="Company"/> onto <c>companies</c>, as V4 and V23 created it. EF never changes the schema.</summary>
internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    /// <summary>The unique index on <c>tax_id_key(tax_id)</c> (V23): a clash is <c>TAX_ID_IN_USE</c>.</summary>
    public const string TaxIdIndex = "companies_tax_id_key";

    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.ToTable("companies");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.LegalName).HasColumnName("legal_name");
        builder.Property(c => c.NameAr).HasColumnName("name_ar");
        builder.Property(c => c.NameEn).HasColumnName("name_en");
        builder.Property(c => c.TaxId).HasColumnName("tax_id");
        builder.Property(c => c.CommercialRegistration).HasColumnName("commercial_registration");
        builder.Property(c => c.ContactName).HasColumnName("contact_name");
        builder.Property(c => c.ContactPhone).HasColumnName("contact_phone");
        builder.Property(c => c.ContactEmail).HasColumnName("contact_email");
        builder.Property(c => c.Status).HasColumnName("status");
        builder.Property(c => c.CooldownMinutes).HasColumnName("cooldown_minutes");
        builder.Property(c => c.LowBalanceThresholdPiastres).HasColumnName("low_balance_threshold_piastres");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.CreatedBy).HasColumnName("created_by");
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");
        builder.Property(c => c.UpdatedBy).HasColumnName("updated_by");

        // A database function a query may call: registered on the model from here, so the module does not
        // edit the shared context.
        builder.Metadata.Model.AddDbFunction(typeof(Company).GetMethod(nameof(Company.TaxIdKey))!).Name = "tax_id_key";
    }
}
