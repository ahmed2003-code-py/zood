using System.Text.Json.Serialization;
using Zawed.Domain;

namespace Zawed.Api.Modules.Fleet;

public enum ImportStatus
{
    VALIDATED,
    QUEUED,
    RUNNING,
    COMPLETED,
    COMPLETED_WITH_ERRORS,
    CANCELLED,
}

public enum ImportMode
{
    STRICT,
    VALID_ROWS_ONLY,
}

public enum ImportRowStatus
{
    VALID,
    INVALID,
    DUPLICATE_IN_FILE,
    EXISTS,
    IMPORTED,
    FAILED,
    SKIPPED,
}

// The records of the import endpoints (Java FleetImportsController), named as the Java records.

/// <param name="Mode">STRICT: every row was valid. VALID_ROWS_ONLY: rows with problems were left out.</param>
/// <param name="InvalidCount">Rows with a problem, a duplicate in the file, or a plate or fleet code already in service.</param>
/// <param name="ExistingCount">Rows whose plate or fleet code was registered by someone else while the import ran.</param>
/// <param name="SameFileImportedBefore">This exact file was already imported for the company.</param>
public sealed record FleetImportResponse(
    Guid Id,
    Guid CompanyId,
    string FileName,
    ImportFormat FileFormat,
    ImportStatus Status,
    ImportMode? Mode,
    int RowCount,
    int ValidCount,
    int InvalidCount,
    int ImportedCount,
    int ExistingCount,
    int FailedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    bool SameFileImportedBefore);

public sealed record FleetImportRowError(string Column, string Code);

public sealed record FleetImportRowResponse(int RowNumber, string? Plate, string? FleetCode, ImportRowStatus Status,
    IReadOnlyList<FleetImportRowError> Errors, Guid? VehicleId);

/// <param name="NextAfter">Pass as after= for the next page; null at the end.</param>
public sealed record FleetImportRowsResponse(IReadOnlyList<FleetImportRowResponse> Items, int? NextAfter);

/// <param name="Mode">STRICT (default): only a file without problems. VALID_ROWS_ONLY: import the valid rows and leave the others out.</param>
public sealed record ConfirmFleetImportRequest(ImportMode? Mode);

public sealed record OnboardingCheck(string Key, bool Done, bool Required, long? Count);

/// <param name="Ready">Every required check is done: the company can fuel.</param>
public sealed record OnboardingResponse(Guid CompanyId, bool Ready, IReadOnlyList<OnboardingCheck> Checks);

/// <summary>
/// A validated row's values, ready to become a vehicle (Java <c>FleetImports.Values</c>), stored as the row's <c>data</c>
/// with the Java property names, so a job validated by either API runs in the other. Money and volume in integers.
/// </summary>
public sealed record ImportValues(
    [property: JsonPropertyName("plate")] string Plate,
    [property: JsonPropertyName("fleetCode")] string? FleetCode,
    [property: JsonPropertyName("make")] string Make,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("fuelType")] FuelType FuelType,
    [property: JsonPropertyName("tankCapacityMl")] long TankCapacityMl,
    [property: JsonPropertyName("departmentId")] Guid? DepartmentId,
    [property: JsonPropertyName("tier")] Tier Tier,
    [property: JsonPropertyName("vipMonthlyCeilingMl")] long? VipMonthlyCeilingMl,
    [property: JsonPropertyName("dailyLimitMl")] long? DailyLimitMl,
    [property: JsonPropertyName("weeklyLimitMl")] long? WeeklyLimitMl,
    [property: JsonPropertyName("monthlyLimitMl")] long? MonthlyLimitMl);
