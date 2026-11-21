using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Modules.Tenancy;

public enum DepartmentStatus
{
    ACTIVE,
    ARCHIVED,
}

/// <summary>
/// A department of a company (table <c>departments</c>, V4). <see cref="ICompanyOwned"/>: EF filters it to the
/// transaction's company and refuses to write another company's row, before row-level security has to.
/// </summary>
public sealed class Department : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Monitoring only: a budget never blocks a fueling (approved decision; limits block).</summary>
    public long? MonthlyBudgetPiastres { get; set; }

    public DepartmentStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    /// <summary>One active department per name in a company, compared on <c>lower(name)</c> (V4).</summary>
    public const string NameIndex = "departments_name_key";

    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("departments");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(d => d.CompanyId).HasColumnName("company_id");
        builder.Property(d => d.Name).HasColumnName("name");
        builder.Property(d => d.MonthlyBudgetPiastres).HasColumnName("monthly_budget_piastres");
        builder.Property(d => d.Status).HasColumnName("status");
        builder.Property(d => d.CreatedAt).HasColumnName("created_at");
        builder.Property(d => d.CreatedBy).HasColumnName("created_by");
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at");
        builder.Property(d => d.UpdatedBy).HasColumnName("updated_by");
    }
}
