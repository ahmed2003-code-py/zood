using Microsoft.EntityFrameworkCore;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Tenancy.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;
using Zawed.Domain;

namespace Zawed.Api.Modules.Fleet;

/// <summary>
/// Vehicles and their limit rules (PRD §4.3, §4.5, §6.3; Java <c>Vehicles</c>). Ops registers and edits; the company
/// reads. A vehicle never leaves its company: the company is fixed at creation, and the database refuses a department,
/// tag or limit from another company. Every change locks the vehicle row first (<see cref="LockAsync"/>), so changes to
/// one vehicle run one at a time, and is audited in the same transaction.
/// </summary>
public sealed class Vehicles(IDbSession session, TimeProvider clock, ICompanyDirectory companies, IAuditLog audit)
{
    /// <summary>The largest tank accepted: the ceiling of one fueling (V14).</summary>
    public const long MaxTankMl = 1_500_000;

    /// <summary>A vehicle's editable values, as Ops typed them (Java <c>Vehicles.Draft</c>).</summary>
    internal sealed record Draft(Guid? DepartmentId, string Plate, string Make, string Model, int? Year, FuelType FuelType,
        long TankCapacityMl, Tier Tier, long? VipMonthlyCeilingMl, string? FleetCode);

    private DbTx Tx => session.Current;
    private ZawedDbContext Db => Tx.Db();

    // ------------------------------------------------------------------------------------------------- queries

    /// <summary>Newest first; <paramref name="query"/> matches the plate whatever its spelling (<see cref="PlateKey.SearchKey"/>).</summary>
    public async Task<Result<Page<VehicleResponse>>> ListAsync(Guid? companyId, Guid? departmentId, VehicleStatus? status, string? query,
        string? cursor, int? limit, bool forCompany, CancellationToken ct)
    {
        var size = Page<VehicleResponse>.Limit(limit);
        var cleaned = Sql.Clean(query);
        var searchKey = cleaned is null ? null : PlateKey.SearchKey(cleaned);
        if (searchKey is { Length: 0 })
        {
            return new Page<VehicleResponse>([], null); // nothing a plate could contain: no match, not every vehicle
        }
        var after = Cursors.Parse(cursor);
        var vehicles = Db.Set<Vehicle>().AsQueryable();
        if (companyId is { } company)
        {
            vehicles = vehicles.Where(v => v.CompanyId == company);
        }
        if (departmentId is { } department)
        {
            vehicles = vehicles.Where(v => v.DepartmentId == department);
        }
        if (status is { } wanted)
        {
            vehicles = vehicles.Where(v => v.Status == wanted);
        }
        if (searchKey is not null)
        {
            // Both sides without tatweels (U+0640 = chr(1600)), so ه and هـ are the same letter to search. The key holds
            // letters and digits only, so it needs no LIKE escaping.
            var pattern = "%" + searchKey + "%";
            vehicles = vehicles.Where(v => EF.Functions.Like(v.PlateKey.Replace(PlateKey.Tatweel, ""), pattern));
        }
        if (after is { } last)
        {
            vehicles = vehicles.Where(v => v.Id < last);
        }
        var rows = await vehicles.OrderByDescending(v => v.Id).Take(size + 1).ToListAsync(ct);
        Func<Vehicle, VehicleResponse> map = forCompany ? VehicleResponse.ForCompany : VehicleResponse.Of;
        var page = rows.Select(map).ToList();
        return Page<VehicleResponse>.Of(page, size, v => v.Id);
    }

    /// <summary>A vehicle with its tags and active limits (the detail screen); the company does not see the VIP ceiling.</summary>
    public async Task<Result<VehicleDetail>> DetailAsync(Guid id, bool forCompany, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is not { } vehicle)
        {
            return Result<VehicleDetail>.NotFound("Vehicle");
        }
        var tags = await Db.Set<RfidTag>().Where(t => t.VehicleId == id).OrderByDescending(t => t.Id).Take(100).ToListAsync(ct);
        return new VehicleDetail(forCompany ? VehicleResponse.ForCompany(vehicle) : VehicleResponse.Of(vehicle),
            tags.Select(TagResponse.Of).ToList(), (await ActiveLimitsAsync(id, ct)).Select(LimitResponse.Of).ToList());
    }

    internal Task<Vehicle?> FindAsync(Guid id, CancellationToken ct) => Db.Set<Vehicle>().SingleOrDefaultAsync(v => v.Id == id, ct);

    private Task<List<LimitRule>> ActiveLimitsAsync(Guid vehicleId, CancellationToken ct) =>
        Db.Set<LimitRule>().Where(r => r.VehicleId == vehicleId && r.Active).OrderBy(r => r.Kind).ThenBy(r => r.Period).ToListAsync(ct);

    // ------------------------------------------------------------------------------------------------- changes

    internal async Task<Result<VehicleResponse>> CreateAsync(ZawedPrincipal actor, Guid companyId, Draft draft, CancellationToken ct)
    {
        if (await companies.FindAsync(companyId, ct) is null)
        {
            return Result<VehicleResponse>.Fail(400, "COMPANY_NOT_FOUND", "No such company", "companyId");
        }
        if (await CheckDraftAsync(companyId, draft, ct) is { } invalid)
        {
            return Result<VehicleResponse>.From(invalid);
        }
        var now = clock.GetUtcNow();
        var vehicle = new Vehicle
        {
            Id = Guid.CreateVersion7(now), CompanyId = companyId, Status = VehicleStatus.ACTIVE,
            CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId,
        };
        Apply(vehicle, draft);
        Db.Add(vehicle);
        if (await SaveAsync(ct) is { } refused)
        {
            return Result<VehicleResponse>.From(refused);
        }
        await audit.RecordAsync(Tx, ActorOf(actor), "VEHICLE_CREATED", "VEHICLE", vehicle.Id, Scope.Company(companyId),
            new Dictionary<string, object?> { ["plate"] = Sql.Clean(draft.Plate), ["tier"] = draft.Tier.ToString() }, ct);
        return Result<VehicleResponse>.Created(VehicleResponse.Of((await FindAsync(vehicle.Id, ct))!));
    }

    internal async Task<Result<VehicleResponse>> UpdateAsync(ZawedPrincipal actor, Guid id, Draft draft, CancellationToken ct)
    {
        if (await LockAsync(id, ct) is not { } vehicle)
        {
            return Result<VehicleResponse>.NotFound("Vehicle");
        }
        if (vehicle.Status == VehicleStatus.DECOMMISSIONED)
        {
            return Decommissioned<VehicleResponse>();
        }
        if (await CheckDraftAsync(vehicle.CompanyId, draft, ct) is { } invalid)
        {
            return Result<VehicleResponse>.From(invalid);
        }
        var (plate, tier, fuel) = (vehicle.Plate, vehicle.Tier, vehicle.FuelType);
        Apply(vehicle, draft);
        vehicle.UpdatedAt = clock.GetUtcNow();
        vehicle.UpdatedBy = actor.UserId;
        if (await SaveAsync(ct) is { } refused)
        {
            return Result<VehicleResponse>.From(refused);
        }
        var change = new Dictionary<string, object?>();
        if (plate != Sql.Clean(draft.Plate))
        {
            change["plate"] = new Dictionary<string, object?> { ["from"] = plate, ["to"] = Sql.Clean(draft.Plate) };
        }
        if (tier != draft.Tier)
        {
            change["tier"] = new Dictionary<string, object?> { ["from"] = tier.ToString(), ["to"] = draft.Tier.ToString() };
        }
        if (fuel != draft.FuelType)
        {
            change["fuelType"] = new Dictionary<string, object?> { ["from"] = fuel.ToString(), ["to"] = draft.FuelType.ToString() };
        }
        await audit.RecordAsync(Tx, ActorOf(actor), "VEHICLE_UPDATED", "VEHICLE", id, Scope.Company(vehicle.CompanyId), change, ct);
        return VehicleResponse.Of((await FindAsync(id, ct))!);
    }

    /// <summary>
    /// Suspends, reactivates or decommissions a vehicle. Decommissioning is final: it frees the plate, deactivates the
    /// vehicle's tags and retires its limits in the same transaction.
    /// </summary>
    internal async Task<Result<VehicleResponse>> SetStatusAsync(ZawedPrincipal actor, Guid id, VehicleStatus status, string reason,
        CancellationToken ct)
    {
        if (await LockAsync(id, ct) is not { } vehicle)
        {
            return Result<VehicleResponse>.NotFound("Vehicle");
        }
        if (vehicle.Status == status)
        {
            return VehicleResponse.Of(vehicle);
        }
        if (vehicle.Status == VehicleStatus.DECOMMISSIONED)
        {
            return Decommissioned<VehicleResponse>();
        }
        var before = vehicle.Status;
        var now = clock.GetUtcNow();
        vehicle.Status = status;
        vehicle.UpdatedAt = now;
        vehicle.UpdatedBy = actor.UserId;
        await Db.SaveChangesAsync(ct);
        var tags = 0;
        if (status == VehicleStatus.DECOMMISSIONED)
        {
            tags = await Tx.ExecuteAsync("""
                update rfid_tags set status = 'DEACTIVATED', status_reason = 'Vehicle decommissioned',
                       updated_at = @now, updated_by = @actor
                where vehicle_id = @id and status in ('ACTIVE', 'REPLACEMENT_PENDING')
                """, new { id, now = now.UtcDateTime, actor = actor.UserId }, ct);
            await Tx.ExecuteAsync("""
                update limit_rules set active = false, deactivated_at = @now, deactivated_by = @actor
                where vehicle_id = @id and active
                """, new { id, now = now.UtcDateTime, actor = actor.UserId }, ct);
        }
        await audit.RecordAsync(Tx, ActorOf(actor), "VEHICLE_STATUS_CHANGED", "VEHICLE", id, Scope.Company(vehicle.CompanyId),
            new Dictionary<string, object?>
            {
                ["from"] = before.ToString(), ["to"] = status.ToString(), ["reason"] = reason, ["tagsDeactivated"] = tags,
            }, ct);
        return VehicleResponse.Of((await FindAsync(id, ct))!);
    }

    /// <summary>The limit set, then the vehicle's active limits (what <c>setVehicleLimit</c> answers).</summary>
    internal async Task<Result<IReadOnlyList<LimitResponse>>> SetLimitAndListAsync(ZawedPrincipal actor, Guid vehicleId, LimitKind kind,
        LimitPeriod period, long maxValue, CancellationToken ct)
    {
        var set = await SetLimitAsync(actor, vehicleId, kind, period, maxValue, ct);
        if (!set.IsSuccess)
        {
            return Result<IReadOnlyList<LimitResponse>>.From(set);
        }
        return (await ActiveLimitsAsync(vehicleId, ct)).Select(LimitResponse.Of).ToList();
    }

    /// <summary>
    /// Sets the one active rule for a kind and period: the previous rule is retired, never edited, so the history of what
    /// applied when stays readable.
    /// </summary>
    internal async Task<Result<LimitResponse>> SetLimitAsync(ZawedPrincipal actor, Guid vehicleId, LimitKind kind, LimitPeriod period,
        long maxValue, CancellationToken ct)
    {
        if (await LockAsync(vehicleId, ct) is not { } vehicle)
        {
            return Result<LimitResponse>.NotFound("Vehicle");
        }
        if (vehicle.Status == VehicleStatus.DECOMMISSIONED)
        {
            return Decommissioned<LimitResponse>();
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var previous = (await Tx.QueryAsync<long>("""
            update limit_rules set active = false, deactivated_at = @now, deactivated_by = @actor
            where vehicle_id = @vehicle and kind = @kind and period = @period and active
            returning max_value
            """, new { vehicle = vehicleId, kind = kind.ToString(), period = period.ToString(), now, actor = actor.UserId }, ct)).ToList();
        var id = Guid.CreateVersion7(clock.GetUtcNow());
        await Tx.ExecuteAsync("""
            insert into limit_rules (id, company_id, vehicle_id, kind, period, max_value, active, created_at, created_by)
            values (@id, @company, @vehicle, @kind, @period, @max, true, @now, @actor)
            """, new
        {
            id, company = vehicle.CompanyId, vehicle = vehicleId, kind = kind.ToString(), period = period.ToString(), max = maxValue, now,
            actor = actor.UserId,
        }, ct);
        var change = new Dictionary<string, object?> { ["kind"] = kind.ToString(), ["period"] = period.ToString(), ["to"] = maxValue };
        if (previous.Count > 0)
        {
            change["from"] = previous[0];
        }
        await audit.RecordAsync(Tx, ActorOf(actor), "LIMIT_SET", "VEHICLE", vehicleId, Scope.Company(vehicle.CompanyId), change, ct);
        return LimitResponse.Of(await Db.Set<LimitRule>().SingleAsync(r => r.Id == id, ct));
    }

    internal async Task<Result> RemoveLimitAsync(ZawedPrincipal actor, Guid vehicleId, Guid limitId, CancellationToken ct)
    {
        if (await LockAsync(vehicleId, ct) is not { } vehicle)
        {
            return Result.NotFound("Vehicle");
        }
        var removed = (await Tx.QueryAsync<(string Kind, string Period, long MaxValue)>("""
            update limit_rules set active = false, deactivated_at = @now, deactivated_by = @actor
            where id = @id and vehicle_id = @vehicle and active
            returning kind, period, max_value
            """, new { id = limitId, vehicle = vehicleId, now = clock.GetUtcNow().UtcDateTime, actor = actor.UserId }, ct)).ToList();
        if (removed.Count == 0)
        {
            return Result.NotFound("Limit");
        }
        var rule = removed[0];
        await audit.RecordAsync(Tx, ActorOf(actor), "LIMIT_REMOVED", "VEHICLE", vehicleId, Scope.Company(vehicle.CompanyId),
            new Dictionary<string, object?> { ["kind"] = rule.Kind, ["period"] = rule.Period, ["from"] = rule.MaxValue }, ct);
        return Result.NoContent();
    }

    // ------------------------------------------------------------------------------------------------- helpers

    /// <summary>
    /// Reads the vehicle and holds its row until commit (<c>FOR UPDATE</c>), so changes to one vehicle run one at a
    /// time: its tags and limits are changed only under this lock. Tracked, so a change is saved by
    /// <see cref="ZawedDbContext.SaveChangesAsync(CancellationToken)"/>.
    /// </summary>
    internal Task<Vehicle?> LockAsync(Guid id, CancellationToken ct) =>
        Db.Set<Vehicle>().FromSql($"select * from vehicles where id = {id} for update").AsTracking().SingleOrDefaultAsync(ct);

    private async Task<Result?> CheckDraftAsync(Guid companyId, Draft draft, CancellationToken ct)
    {
        if (draft.TankCapacityMl <= 0 || draft.TankCapacityMl > MaxTankMl)
        {
            return Result.Fail(400, "INVALID_TANK_CAPACITY", "The tank capacity must be between 1 ml and 1,500 L", "tankCapacityMl");
        }
        if (draft.Year is { } year && (year < 1950 || year > clock.GetUtcNow().Year + 1))
        {
            return Result.Fail(400, "INVALID_YEAR", "The model year is not plausible", "year");
        }
        if (draft.Tier != Tier.VIP && draft.VipMonthlyCeilingMl is not null)
        {
            return Result.Fail(400, "VIP_CEILING_ONLY_FOR_VIP", "A monthly ceiling applies to VIP vehicles only", "vipMonthlyCeilingMl");
        }
        if (PlateKey.Of(draft.Plate).Length == 0)
        {
            return Result.Fail(400, "INVALID_PLATE", "The plate has no letters or digits", "plate");
        }
        if (!ValidFleetCode(Sql.Clean(draft.FleetCode)))
        {
            return Result.Fail(400, "INVALID_FLEET_CODE", "The fleet code must not contain control characters", "fleetCode");
        }
        if (draft.DepartmentId is { } department && !await companies.HasActiveDepartmentAsync(companyId, department, ct))
        {
            return Result.Fail(400, "DEPARTMENT_NOT_FOUND", "No such department in this company", "departmentId");
        }
        return null;
    }

    /// <summary>
    /// A fleet code as stored (<see cref="Sql.Clean"/>: whitespace, tabs and line breaks included, becomes one space) must
    /// hold no control character: the database refuses one (V14, <c>[:cntrl:]</c>, Unicode category Cc, the set
    /// <see cref="char.IsControl(char)"/> tests). Checking here turns that refusal into a 400 instead of a 500, and keeps a bulk
    /// import row from failing at insert time.
    /// </summary>
    internal static bool ValidFleetCode(string? cleaned) => cleaned is null || !cleaned.Any(char.IsControl);

    private static void Apply(Vehicle vehicle, Draft draft)
    {
        vehicle.DepartmentId = draft.DepartmentId;
        vehicle.Plate = Sql.Clean(draft.Plate)!;
        vehicle.PlateKey = PlateKey.Of(draft.Plate);
        vehicle.Make = Sql.Clean(draft.Make)!;
        vehicle.Model = Sql.Clean(draft.Model)!;
        vehicle.Year = draft.Year;
        vehicle.FuelType = draft.FuelType;
        vehicle.TankCapacityMl = draft.TankCapacityMl;
        vehicle.Tier = draft.Tier;
        vehicle.VipMonthlyCeilingMl = draft.VipMonthlyCeilingMl;
        vehicle.FleetCode = Sql.Clean(draft.FleetCode);
    }

    /// <summary>Saves; a unique index the row breaks is the Java refusal: the fleet code (V14) or the plate.</summary>
    private async Task<Result?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await Db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return Duplicate(ex);
        }
    }

    internal static Result Duplicate(Exception ex) => DbErrors.Postgres(ex)?.ConstraintName == VehicleConfiguration.FleetCodeIndex
        ? Result.Fail(409, "FLEET_CODE_IN_USE", "Another vehicle of this company in service has this fleet code", "fleetCode")
        : Result.Fail(409, "PLATE_IN_USE", "Another vehicle has this plate", "plate");

    private static Result<T> Decommissioned<T>() => Result<T>.Conflict("VEHICLE_DECOMMISSIONED", "The vehicle is decommissioned");

    internal static AuditActor ActorOf(ZawedPrincipal actor) => new(actor.UserId, actor.Role);
}
