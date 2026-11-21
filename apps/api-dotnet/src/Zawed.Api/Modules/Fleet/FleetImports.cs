using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Tenancy.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;
using Zawed.Domain;

namespace Zawed.Api.Modules.Fleet;

/// <summary>
/// Bulk vehicle import (audit F4, ADR 0026; Java <c>FleetImports</c>): Upload → Validate → Preview → Confirm → Background →
/// Results.
/// <para>The upload is parsed and every row validated in one short request that takes no locks: field rules (the same ones
/// as a single vehicle), duplicates inside the file, plates and fleet codes already in service. Rows are stored normalised;
/// the file is not kept. Confirming queues the job for <see cref="FleetImportWorker"/>; in <c>STRICT</c> mode (the default)
/// only a file without a single problem may be confirmed, in <c>VALID_ROWS_ONLY</c> mode the problem rows are left out, and
/// the API and screen say so. Only one import per company runs at a time.</para>
/// </summary>
public sealed partial class FleetImports(IDbSession session, TimeProvider clock, ICompanyDirectory companies, IAuditLog audit,
    ILogger<FleetImports> log)
{
    /// <summary>How rows and errors are stored as JSON: the Java property names, nulls written, enums by name.</summary>
    internal static readonly JsonSerializerOptions Json = ZawedJson.Options;

    private DbTx Tx => session.Current;
    private ZawedDbContext Db => Tx.Db();

    // -------------------------------------------------------------------------------------------- upload + validate

    internal async Task<Result<FleetImportResponse>> UploadAsync(ZawedPrincipal actor, Guid companyId, string? fileName, byte[] bytes,
        CancellationToken ct)
    {
        if (await companies.FindAsync(companyId, ct) is null)
        {
            return Result<FleetImportResponse>.NotFound("Company");
        }
        var format = FleetImportFile.FormatOf(fileName, bytes);
        var rows = FleetImportFile.Read(format, bytes, log);
        var departments = await DepartmentsAsync(companyId, ct);

        // 1. Field rules, row by row.
        var validated = rows.Select(row => Validate(row, departments)).ToList();
        // 2. Duplicates inside the file: the first occurrence stays, later ones are flagged.
        var plates = new HashSet<string>(StringComparer.Ordinal);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in validated)
        {
            if (v.PlateKey is not null && !plates.Add(v.PlateKey))
            {
                v.Duplicate("plate", "DUPLICATE_PLATE_IN_FILE");
            }
            if (v.FleetCodeKey is not null && !codes.Add(v.FleetCodeKey))
            {
                v.Duplicate("fleet_code", "DUPLICATE_FLEET_CODE_IN_FILE");
            }
        }
        // 3. Already in service: plates platform-wide, fleet codes within the company.
        var takenPlates = (await Tx.QueryAsync<string>("""
            select plate_key from vehicles where status <> 'DECOMMISSIONED' and plate_key = any(@keys)
            """, new { keys = plates.ToArray() }, ct)).ToHashSet(StringComparer.Ordinal);
        var takenCodes = (await Tx.QueryAsync<string>("""
            select upper(fleet_code) from vehicles
            where company_id = @company and status <> 'DECOMMISSIONED' and upper(fleet_code) = any(@codes)
            """, new { company = companyId, codes = codes.ToArray() }, ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var v in validated.Where(v => v.Status is ImportRowStatus.VALID or ImportRowStatus.INVALID))
        {
            if (v.PlateKey is not null && takenPlates.Contains(v.PlateKey))
            {
                v.Exists("plate", "PLATE_IN_USE");
            }
            if (v.FleetCodeKey is not null && takenCodes.Contains(v.FleetCodeKey))
            {
                v.Exists("fleet_code", "FLEET_CODE_IN_USE");
            }
        }

        var valid = validated.Count(v => v.Status == ImportRowStatus.VALID);
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        await Tx.ExecuteAsync("""
            insert into fleet_import_jobs (id, company_id, file_name, file_sha256, file_format, status, row_count,
                                           valid_count, invalid_count, created_by, created_at, updated_at)
            values (@id, @company, @name, @sha, @format, 'VALIDATED', @rows, @valid, @invalid, @actor, @now, @now)
            """, new
        {
            id, company = companyId, name = SafeName(fileName), sha = Sha256(bytes), format = format.ToString(), rows = validated.Count, valid,
            invalid = validated.Count - valid, actor = actor.UserId, now = now.UtcDateTime,
        }, ct);
        await Tx.ExecuteAsync("""
            insert into fleet_import_rows (job_id, row_number, plate, plate_key, fleet_code, data, status, errors)
            values (@job, @row, @plate, @key, @code, cast(@data as jsonb), @status, cast(@errors as jsonb))
            """, validated.Select(v => new
        {
            job = id, row = v.Number, plate = v.Row.Get(ImportColumn.PLATE), key = v.PlateKey, code = v.Row.Get(ImportColumn.FLEET_CODE),
            data = v.Values is null ? JsonSerializer.Serialize(v.RawByKey(), Json) : JsonSerializer.Serialize(v.Values, Json),
            status = v.Status.ToString(), errors = JsonSerializer.Serialize(v.Errors, Json),
        }).ToList(), ct);
        await audit.RecordAsync(Tx, Vehicles.ActorOf(actor), "FLEET_IMPORT_VALIDATED", "FLEET_IMPORT", id, Scope.Company(companyId),
            new Dictionary<string, object?>
            {
                ["fileName"] = SafeName(fileName), ["rows"] = validated.Count, ["valid"] = valid, ["format"] = format.ToString(),
            }, ct);
        return Result<FleetImportResponse>.Created(await ResponseAsync((await FindAsync(id, ct))!, ct));
    }

    /// <summary>One row being validated (Java <c>FleetImports.Validated</c>).</summary>
    private sealed class Validated(ImportRow row)
    {
        public ImportRow Row { get; } = row;
        public int Number => Row.Number;
        public List<FleetImportRowError> Errors { get; } = [];
        public ImportRowStatus Status { get; private set; } = ImportRowStatus.VALID;
        public ImportValues? Values { get; set; }
        public string? PlateKey { get; set; }
        public string? FleetCodeKey { get; set; }

        public void Error(string column, string code)
        {
            Errors.Add(new(column, code));
            Status = ImportRowStatus.INVALID;
            Values = null;
        }

        public void Duplicate(string column, string code)
        {
            Errors.Add(new(column, code));
            if (Status == ImportRowStatus.VALID)
            {
                Status = ImportRowStatus.DUPLICATE_IN_FILE;
            }
        }

        public void Exists(string column, string code)
        {
            Errors.Add(new(column, code));
            if (Status == ImportRowStatus.VALID)
            {
                Status = ImportRowStatus.EXISTS;
            }
        }

        /// <summary>The raw cells by column key, in file order: what an invalid row stores.</summary>
        public Dictionary<string, string> RawByKey() => Row.Cells.ToDictionary(c => FleetImportFile.Info(c.Key).Key, c => c.Value);
    }

    private Validated Validate(ImportRow row, Dictionary<string, Guid> departments)
    {
        var v = new Validated(row);
        foreach (var (column, value) in row.Cells)
        {
            if (value.Length > FleetImportFile.MaxCell)
            {
                v.Error(FleetImportFile.Info(column).Key, "TOO_LONG");
            }
        }
        foreach (var column in FleetImportFile.Columns.Where(c => c.Required && row.Get(c.Column) is null))
        {
            v.Error(column.Key, "REQUIRED");
        }
        var plate = row.Get(ImportColumn.PLATE);
        if (plate is not null)
        {
            var key = PlateKey.Of(plate);
            if (key.Length == 0 || plate.Length > 20)
            {
                v.Error("plate", "INVALID_PLATE");
            }
            else
            {
                v.PlateKey = key;
            }
        }
        // Normalised as the vehicle will store it, so in-file duplicates and the existing-code check see the stored value.
        var fleetCode = Sql.Clean(row.Get(ImportColumn.FLEET_CODE));
        if (fleetCode is not null)
        {
            if (fleetCode.Length > 40)
            {
                v.Error("fleet_code", "TOO_LONG");
            }
            else if (!Vehicles.ValidFleetCode(fleetCode))
            {
                v.Error("fleet_code", "INVALID_FLEET_CODE");
            }
            else
            {
                v.FleetCodeKey = fleetCode.ToUpperInvariant();
            }
        }
        foreach (var text in new[] { ImportColumn.MAKE, ImportColumn.MODEL })
        {
            if (row.Get(text) is { Length: > 60 })
            {
                v.Error(FleetImportFile.Info(text).Key, "TOO_LONG");
            }
        }
        int? year = null;
        if (row.Get(ImportColumn.YEAR) is { } yearText)
        {
            year = Integer(yearText);
            if (year is null || year < 1950 || year > clock.GetUtcNow().Year + 1)
            {
                v.Error("year", "INVALID_YEAR");
            }
        }
        var fuelText = row.Get(ImportColumn.FUEL_TYPE);
        var fuel = fuelText is null ? null : Fuel(fuelText);
        if (fuelText is not null && fuel is null)
        {
            v.Error("fuel_type", "INVALID_FUEL_TYPE");
        }
        var tankText = row.Get(ImportColumn.TANK_L);
        var tank = tankText is null ? null : Litres(tankText);
        if (tankText is not null && (tank is null || tank <= 0 || tank > Vehicles.MaxTankMl))
        {
            v.Error("tank_capacity_l", "INVALID_TANK_CAPACITY");
        }
        Guid? department = null;
        if (row.Get(ImportColumn.DEPARTMENT) is { } departmentName)
        {
            if (departments.TryGetValue(FleetImportFile.Normalize(departmentName), out var found))
            {
                department = found;
            }
            else
            {
                v.Error("department", "UNKNOWN_DEPARTMENT");
            }
        }
        Tier? tier = Tier.NORMAL;
        if (row.Get(ImportColumn.TIER) is { } tierText)
        {
            tier = TierOf(tierText);
            if (tier is null)
            {
                v.Error("tier", "INVALID_TIER");
            }
        }
        long? ceiling = null;
        if (row.Get(ImportColumn.VIP_CEILING_L) is { } ceilingText)
        {
            ceiling = Litres(ceilingText);
            if (ceiling is null || ceiling <= 0)
            {
                v.Error("vip_monthly_ceiling_l", "INVALID_NUMBER");
            }
            else if (tier != Tier.VIP)
            {
                v.Error("vip_monthly_ceiling_l", "VIP_CEILING_ONLY_FOR_VIP");
            }
        }
        var limits = new long?[3];
        ImportColumn[] limitColumns = [ImportColumn.DAILY_L, ImportColumn.WEEKLY_L, ImportColumn.MONTHLY_L];
        for (var i = 0; i < 3; i++)
        {
            if (row.Get(limitColumns[i]) is { } limitText)
            {
                limits[i] = Litres(limitText);
                if (limits[i] is null || limits[i] <= 0 || limits[i] > 100_000_000L)
                {
                    v.Error(FleetImportFile.Info(limitColumns[i]).Key, "INVALID_LIMIT");
                }
            }
        }
        if (v.Status == ImportRowStatus.VALID)
        {
            v.Values = new ImportValues(plate!, fleetCode, row.Get(ImportColumn.MAKE)!, row.Get(ImportColumn.MODEL)!, year, fuel!.Value,
                tank!.Value, department, tier!.Value, ceiling, limits[0], limits[1], limits[2]);
        }
        return v;
    }

    // ------------------------------------------------------------------------------------------------------ reading

    internal async Task<Result<FleetImportResponse>> GetAsync(Guid id, CancellationToken ct) =>
        await FindAsync(id, ct) is { } job ? await ResponseAsync(job, ct) : Result<FleetImportResponse>.NotFound("Import");

    internal Task<FleetImportJob?> FindAsync(Guid id, CancellationToken ct) => Db.Set<FleetImportJob>().SingleOrDefaultAsync(j => j.Id == id, ct);

    internal async Task<Result<Page<FleetImportResponse>>> ListAsync(Guid? companyId, string? cursor, int? limit, CancellationToken ct)
    {
        var size = Page<FleetImportResponse>.Limit(limit);
        var after = Cursors.Parse(cursor);
        var jobs = Db.Set<FleetImportJob>().AsQueryable();
        if (companyId is { } company)
        {
            jobs = jobs.Where(j => j.CompanyId == company);
        }
        if (after is { } last)
        {
            jobs = jobs.Where(j => j.Id < last);
        }
        var rows = await jobs.OrderByDescending(j => j.Id).Take(size + 1).ToListAsync(ct);
        return Page<FleetImportResponse>.Of(rows.Select(j => Response(j, false)).ToList(), size, j => j.Id);
    }

    /// <summary>Rows in file order; <paramref name="after"/> is the last row number seen.</summary>
    internal async Task<Result<FleetImportRowsResponse>> RowsAsync(Guid jobId, ImportRowStatus? status, bool problemsOnly, int after, int limit,
        CancellationToken ct)
    {
        if (await FindAsync(jobId, ct) is null)
        {
            return Result<FleetImportRowsResponse>.NotFound("Import");
        }
        var size = Math.Min(Math.Max(limit, 1), 500);
        var rows = await PageOfRowsAsync(jobId, status, problemsOnly, after, size, ct);
        return new FleetImportRowsResponse(rows, rows.Count == size ? rows[^1].RowNumber : null);
    }

    private async Task<List<FleetImportRowResponse>> PageOfRowsAsync(Guid jobId, ImportRowStatus? status, bool problemsOnly, int after, int size,
        CancellationToken ct)
    {
        var rows = Db.Set<FleetImportRow>().Where(r => r.JobId == jobId && r.RowNumber > after);
        if (status is { } wanted)
        {
            rows = rows.Where(r => r.Status == wanted);
        }
        if (problemsOnly)
        {
            rows = rows.Where(r => r.Status != ImportRowStatus.VALID && r.Status != ImportRowStatus.IMPORTED);
        }
        var found = await rows.OrderBy(r => r.RowNumber).Take(size).ToListAsync(ct);
        return found.Select(r => new FleetImportRowResponse(r.RowNumber, r.Plate, r.FleetCode, r.Status,
            JsonSerializer.Deserialize<List<FleetImportRowError>>(r.Errors, Json)!, r.VehicleId)).ToList();
    }

    /// <summary>Whether this exact file was already imported for the company (a warning, not a refusal).</summary>
    private Task<bool> ImportedBeforeAsync(FleetImportJob job, CancellationToken ct) => Tx.QuerySingleAsync<bool>("""
        select exists (select 1 from fleet_import_jobs
                       where company_id = @company and file_sha256 = @sha and id <> @id
                         and status in ('COMPLETED', 'COMPLETED_WITH_ERRORS'))
        """, new { company = job.CompanyId, sha = job.FileSha256, id = job.Id }, ct);

    private async Task<FleetImportResponse> ResponseAsync(FleetImportJob job, CancellationToken ct) =>
        Response(job, await ImportedBeforeAsync(job, ct));

    private static FleetImportResponse Response(FleetImportJob j, bool importedBefore) => new(j.Id, j.CompanyId, j.FileName, j.FileFormat,
        j.Status, j.Mode, j.RowCount, j.ValidCount, j.InvalidCount, j.ImportedCount, j.ExistingCount, j.FailedCount, j.CreatedAt,
        j.ConfirmedAt, j.StartedAt, j.FinishedAt, importedBefore);

    // ---------------------------------------------------------------------------------------------- confirm / cancel

    internal async Task<Result<FleetImportResponse>> ConfirmAsync(ZawedPrincipal actor, Guid id, ImportMode? mode, CancellationToken ct)
    {
        if (await LockAsync(id, ct) is not { } job)
        {
            return Result<FleetImportResponse>.NotFound("Import");
        }
        if (job.Status != ImportStatus.VALIDATED)
        {
            if (job.Status is ImportStatus.QUEUED or ImportStatus.RUNNING or ImportStatus.COMPLETED or ImportStatus.COMPLETED_WITH_ERRORS)
            {
                return await ResponseAsync(job, ct); // a repeated confirmation: already queued or done, exactly once
            }
            return Result<FleetImportResponse>.Fail(409, "IMPORT_NOT_CONFIRMABLE", "This import can no longer be confirmed", null,
                new Dictionary<string, object> { ["status"] = job.Status.ToString() });
        }
        var chosen = mode ?? ImportMode.STRICT;
        if (chosen == ImportMode.STRICT && job.InvalidCount > 0)
        {
            return Result<FleetImportResponse>.Fail(409, "IMPORT_HAS_PROBLEMS",
                "Some rows have problems: fix the file, or confirm importing only the valid rows", null,
                new Dictionary<string, object> { ["invalid"] = job.InvalidCount });
        }
        if (job.ValidCount == 0)
        {
            return Result<FleetImportResponse>.Fail(409, "IMPORT_NOTHING_TO_IMPORT", "No row can be imported");
        }
        var now = clock.GetUtcNow().UtcDateTime;
        try
        {
            await Tx.ExecuteAsync("""
                update fleet_import_jobs set status = 'QUEUED', mode = @mode, confirmed_by = @actor,
                       confirmed_by_role = @role, confirmed_at = @now, updated_at = @now
                where id = @id and status = 'VALIDATED'
                """, new { mode = chosen.ToString(), actor = actor.UserId, role = actor.Role.ToString(), now, id }, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return Result<FleetImportResponse>.Fail(409, "IMPORT_ALREADY_RUNNING",
                "Another import for this company is running; wait for it to finish");
        }
        if (chosen == ImportMode.VALID_ROWS_ONLY)
        {
            await Tx.ExecuteAsync("update fleet_import_rows set status = 'SKIPPED', processed_at = @now where job_id = @job and status <> 'VALID'",
                new { now, job = id }, ct);
        }
        await audit.RecordAsync(Tx, Vehicles.ActorOf(actor), "FLEET_IMPORT_CONFIRMED", "FLEET_IMPORT", id, Scope.Company(job.CompanyId),
            new Dictionary<string, object?> { ["mode"] = chosen.ToString(), ["valid"] = job.ValidCount, ["invalid"] = job.InvalidCount }, ct);
        return await GetAsync(id, ct);
    }

    internal async Task<Result<FleetImportResponse>> CancelAsync(ZawedPrincipal actor, Guid id, CancellationToken ct)
    {
        if (await LockAsync(id, ct) is not { } job)
        {
            return Result<FleetImportResponse>.NotFound("Import");
        }
        if (job.Status != ImportStatus.VALIDATED)
        {
            return Result<FleetImportResponse>.Fail(409, "IMPORT_NOT_CANCELLABLE", "Only an unconfirmed import is cancelled", null,
                new Dictionary<string, object> { ["status"] = job.Status.ToString() });
        }
        await Tx.ExecuteAsync("update fleet_import_jobs set status = 'CANCELLED', updated_at = @now where id = @id",
            new { now = clock.GetUtcNow().UtcDateTime, id }, ct);
        await audit.RecordAsync(Tx, Vehicles.ActorOf(actor), "FLEET_IMPORT_CANCELLED", "FLEET_IMPORT", id, Scope.Company(job.CompanyId),
            new Dictionary<string, object?>(), ct);
        var cancelled = (await FindAsync(id, ct))!;
        return Response(cancelled, false);
    }

    private Task<FleetImportJob?> LockAsync(Guid id, CancellationToken ct) =>
        Db.Set<FleetImportJob>().FromSql($"select * from fleet_import_jobs where id = {id} for update").SingleOrDefaultAsync(ct);

    // -------------------------------------------------------------------------------------------- reports and templates

    /// <summary>The rows that need attention, as CSV for a spreadsheet; every cell defused against formulas.</summary>
    internal async Task<Result<byte[]>> ErrorReportAsync(Guid jobId, CancellationToken ct)
    {
        if (await FindAsync(jobId, ct) is null)
        {
            return Result<byte[]>.NotFound("Import");
        }
        var csv = new StringBuilder("﻿row,plate,fleet_code,status,errors\r\n");
        var after = 0;
        List<FleetImportRowResponse> page;
        do
        {
            page = await PageOfRowsAsync(jobId, null, true, after, 500, ct);
            foreach (var row in page)
            {
                var errors = string.Join("; ", row.Errors.Select(e => e.Column + ": " + e.Code));
                csv.Append(row.RowNumber).Append(',').Append(Field(row.Plate)).Append(',').Append(Field(row.FleetCode))
                    .Append(',').Append(row.Status.ToString()).Append(',').Append(Field(errors)).Append("\r\n");
                after = row.RowNumber;
            }
        }
        while (page.Count == 500);
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    public static byte[] Template(ImportFormat format, bool arabic)
    {
        var headers = FleetImportFile.Columns.Select(c => arabic ? c.ArabicHeader : c.Key).ToList();
        List<string> example = ["1234 ط ب ع", "TRK-001", "Toyota", "Hilux", "2022", arabic ? "سولار" : "DIESEL", "80",
            arabic ? "التوزيع" : "Distribution", arabic ? "عادي" : "NORMAL", "", "60", "", "1200"];
        if (format == ImportFormat.CSV)
        {
            var csv = new StringBuilder("﻿");
            csv.Append(string.Join(",", headers.Select(Field))).Append("\r\n");
            csv.Append(string.Join(",", example.Select(Field))).Append("\r\n");
            return Encoding.UTF8.GetBytes(csv.ToString());
        }
        string[] lines = arabic
            ? ["احذف صف المثال قبل الرفع.", "الأعمدة المطلوبة: اللوحة، الماركة، الموديل، نوع الوقود، سعة التانك (لتر).",
                "نوع الوقود: بنزين 80، بنزين 92، بنزين 95، سولار، غاز طبيعي (أو GAS_80, GAS_92, GAS_95, DIESEL, CNG).",
                "الفئة: عادي أو VIP. الحدود باللتر.", "الإدارة: اسم إدارة موجودة في الشركة.", "حتى 5,000 مركبة في الملف."]
            : ["Delete the example row before uploading.", "Required: plate, make, model, fuel_type, tank_capacity_l.",
                "fuel_type: GAS_80, GAS_92, GAS_95, DIESEL, CNG (Arabic names accepted).", "tier: NORMAL or VIP. Limits are in litres.",
                "department: the name of an existing department.", "At most 5,000 vehicles per file."];
        return Xlsx.Write(
        [
            new Xlsx.Sheet(arabic ? "المركبات" : "Vehicles", [headers, example], [.. headers.Select(_ => 20)], true),
            new Xlsx.Sheet(arabic ? "تعليمات" : "Instructions", [.. lines.Select(l => (IReadOnlyList<string>)[l])], [100], false),
        ]);
    }

    // ------------------------------------------------------------------------------------------------------ readiness

    /// <summary>Whether a company is ready to fuel, derived from its data: details, policy, vehicles, tags, wallet, users.</summary>
    internal async Task<Result<OnboardingResponse>> OnboardingAsync(Guid id, CancellationToken ct)
    {
        if (await companies.FindAsync(id, ct) is null)
        {
            return Result<OnboardingResponse>.NotFound("Company");
        }
        var facts = (IDictionary<string, object>)await Tx.QuerySingleAsync<dynamic>("""
            select (c.contact_name is not null and (c.contact_phone is not null or c.contact_email is not null)) as contact,
                   (c.tax_id is not null or c.commercial_registration is not null) as registration,
                   c.status = 'ACTIVE' as active,
                   (select count(*) from departments d where d.company_id = c.id and d.status = 'ACTIVE') as departments,
                   (select count(*) from vehicles v where v.company_id = c.id and v.status = 'ACTIVE') as vehicles,
                   (select count(distinct t.vehicle_id) from rfid_tags t join vehicles v on v.id = t.vehicle_id
                     where t.company_id = c.id and t.status = 'ACTIVE' and v.status = 'ACTIVE') as tagged,
                   (select count(distinct l.vehicle_id) from limit_rules l where l.company_id = c.id and l.active) as limited,
                   coalesce((select w.balance_piastres from company_wallets w where w.company_id = c.id), -1) as balance,
                   (select count(*) from users u where u.scope_type = 'COMPANY' and u.scope_id = c.id
                     and u.status = 'ACTIVE') as users
            from companies c where c.id = @id
            """, new { id }, ct);
        long Number(string key) => Convert.ToInt64(facts[key], System.Globalization.CultureInfo.InvariantCulture);
        bool Fact(string key) => facts[key] is true;
        var vehicles = Number("vehicles");
        var tagged = Number("tagged");
        var balance = Number("balance");
        List<OnboardingCheck> checks =
        [
            new("ACTIVE", Fact("active"), true, null),
            new("CONTACT", Fact("contact"), false, null),
            new("REGISTRATION", Fact("registration"), false, null),
            new("DEPARTMENTS", Number("departments") > 0, false, Number("departments")),
            new("VEHICLES", vehicles > 0, true, vehicles),
            new("TAGS", vehicles > 0 && tagged == vehicles, true, tagged),
            new("LIMITS", Number("limited") > 0, false, Number("limited")),
            new("WALLET_FUNDED", balance > 0, true, Math.Max(balance, 0)),
            new("COMPANY_USERS", Number("users") > 0, false, Number("users")),
        ];
        return new OnboardingResponse(id, checks.Where(c => c.Required).All(c => c.Done), checks);
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private async Task<Dictionary<string, Guid>> DepartmentsAsync(Guid companyId, CancellationToken ct)
    {
        var byName = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (id, name) in await Tx.QueryAsync<(Guid, string)>(
            "select id, name from departments where company_id = @company and status = 'ACTIVE'", new { company = companyId }, ct))
        {
            byName[FleetImportFile.Normalize(name)] = id;
        }
        return byName;
    }

    public static FuelType? Fuel(string value) => FleetImportFile.Normalize(value).ToUpperInvariant() switch
    {
        "GAS80" or "80" or "بنزين80" => FuelType.GAS_80,
        "GAS92" or "92" or "بنزين92" => FuelType.GAS_92,
        "GAS95" or "95" or "بنزين95" => FuelType.GAS_95,
        "DIESEL" or "سولار" or "ديزل" or "سولر" => FuelType.DIESEL,
        "CNG" or "غاز" or "غازطبيعي" => FuelType.CNG,
        _ => null,
    };

    public static Tier? TierOf(string value) => FleetImportFile.Normalize(value).ToUpperInvariant() switch
    {
        "NORMAL" or "عادي" or "عادى" => Tier.NORMAL,
        "VIP" or "مميز" => Tier.VIP,
        _ => null,
    };

    /// <summary>Litres as typed (Western or Arabic digits, up to 3 decimals) to integer millilitres, in integer arithmetic.</summary>
    public static long? Litres(string value)
    {
        var digits = JavaText.Strip(ToWesternDigits(value).Replace('٫', '.').Replace(',', '.'));
        var match = LitresShape().Match(digits);
        if (!match.Success)
        {
            return null;
        }
        var whole = long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var fraction = match.Groups[2].Success ? match.Groups[2].Value.PadRight(3, '0') : "000";
        return whole * 1000 + long.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static int? Integer(string value)
    {
        var digits = JavaText.Strip(ToWesternDigits(value));
        var match = IntegerShape().Match(digits);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static string ToWesternDigits(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            output.Append(c switch
            {
                >= '٠' and <= '٩' => (char)('0' + c - 0x0660),
                >= '۰' and <= '۹' => (char)('0' + c - 0x06F0),
                _ => c,
            });
        }
        return output.ToString();
    }

    /// <summary>A CSV field, quoted when needed and defused when a spreadsheet would read it as a formula.</summary>
    public static string Field(string? value)
    {
        if (value is null)
        {
            return "";
        }
        var v = value;
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0]))
        {
            v = "'" + v;
        }
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
        {
            v = "\"" + v.Replace("\"", "\"\"") + "\"";
        }
        return v;
    }

    /// <summary>The name stored: ASCII control characters and slashes replaced (Java's <c>\p{Cntrl}</c>), at most 255 characters.</summary>
    private static string SafeName(string? fileName)
    {
        var name = fileName is null ? "import" : JavaText.Strip(UnsafeNameCharacters().Replace(fileName, "_"));
        if (name.Length == 0)
        {
            name = "import";
        }
        return name.Length > 255 ? name[^255..] : name;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [GeneratedRegex(@"\A([0-9]{1,9})(?:\.([0-9]{1,3}))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex LitresShape();

    [GeneratedRegex(@"\A([0-9]{1,4})(?:\.0+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerShape();

    [GeneratedRegex("[\u0000-\u001F\u007F/\\\\]", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeNameCharacters();
}
