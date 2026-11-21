using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zawed.Api.Modules.Fleet;

/// <summary>
/// A bulk vehicle import (table <c>fleet_import_jobs</c>, V15; Java <c>FleetImports.Job</c>): one file for one company,
/// validated, confirmed, then run by <see cref="FleetImportWorker"/>. Ops only: row-level security shows it to the platform
/// scope alone, never to the company. Read through EF; every change is the Java statement on the transaction.
/// </summary>
public sealed class FleetImportJob
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string FileName { get; set; } = "";
    public ImportFormat FileFormat { get; set; }
    public string FileSha256 { get; set; } = "";
    public ImportStatus Status { get; set; }
    public ImportMode? Mode { get; set; }
    public int RowCount { get; set; }
    public int ValidCount { get; set; }
    public int InvalidCount { get; set; }
    public int ImportedCount { get; set; }
    public int ExistingCount { get; set; }
    public int FailedCount { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

internal sealed class FleetImportJobConfiguration : IEntityTypeConfiguration<FleetImportJob>
{
    /// <summary>One import at a time per company (V15).</summary>
    public const string OneActiveIndex = "fleet_import_jobs_one_active";

    public void Configure(EntityTypeBuilder<FleetImportJob> builder)
    {
        builder.ToTable("fleet_import_jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(j => j.CompanyId).HasColumnName("company_id");
        builder.Property(j => j.FileName).HasColumnName("file_name");
        builder.Property(j => j.FileFormat).HasColumnName("file_format");
        builder.Property(j => j.FileSha256).HasColumnName("file_sha256");
        builder.Property(j => j.Status).HasColumnName("status");
        builder.Property(j => j.Mode).HasColumnName("mode");
        builder.Property(j => j.RowCount).HasColumnName("row_count");
        builder.Property(j => j.ValidCount).HasColumnName("valid_count");
        builder.Property(j => j.InvalidCount).HasColumnName("invalid_count");
        builder.Property(j => j.ImportedCount).HasColumnName("imported_count");
        builder.Property(j => j.ExistingCount).HasColumnName("existing_count");
        builder.Property(j => j.FailedCount).HasColumnName("failed_count");
        builder.Property(j => j.CreatedBy).HasColumnName("created_by");
        builder.Property(j => j.CreatedAt).HasColumnName("created_at");
        builder.Property(j => j.ConfirmedBy).HasColumnName("confirmed_by");
        builder.Property(j => j.ConfirmedAt).HasColumnName("confirmed_at");
        builder.Property(j => j.StartedAt).HasColumnName("started_at");
        builder.Property(j => j.FinishedAt).HasColumnName("finished_at");
    }
}

/// <summary>One row of an import file, normalised, with its validation errors and its outcome (table <c>fleet_import_rows</c>).</summary>
public sealed class FleetImportRow
{
    public Guid JobId { get; set; }
    public int RowNumber { get; set; }
    public string? Plate { get; set; }
    public string? FleetCode { get; set; }
    public ImportRowStatus Status { get; set; }

    /// <summary>The row's errors as JSON (<c>[{"column": ..., "code": ...}]</c>).</summary>
    public string Errors { get; set; } = "[]";

    public Guid? VehicleId { get; set; }
}

internal sealed class FleetImportRowConfiguration : IEntityTypeConfiguration<FleetImportRow>
{
    public void Configure(EntityTypeBuilder<FleetImportRow> builder)
    {
        builder.ToTable("fleet_import_rows");
        builder.HasKey(r => new { r.JobId, r.RowNumber });
        builder.Property(r => r.JobId).HasColumnName("job_id");
        builder.Property(r => r.RowNumber).HasColumnName("row_number");
        builder.Property(r => r.Plate).HasColumnName("plate");
        builder.Property(r => r.FleetCode).HasColumnName("fleet_code");
        builder.Property(r => r.Status).HasColumnName("status");
        builder.Property(r => r.Errors).HasColumnName("errors").HasColumnType("jsonb");
        builder.Property(r => r.VehicleId).HasColumnName("vehicle_id");
    }
}
