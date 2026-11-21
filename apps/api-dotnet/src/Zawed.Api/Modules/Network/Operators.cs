using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;
using Zawed.Domain;

namespace Zawed.Api.Modules.Network;

/// <summary>
/// Station operators and their stations (PRD §6.3 Onboard Station; Java <c>network.Operators</c>). Ops onboards and
/// edits; the operator's station manager reads. Every change is audited under the operator's scope. The Java statements
/// run as they are, with Dapper, on the request's transaction (row-level security decides what a scope sees).
/// </summary>
public sealed class Operators(IDbSession session, IAuditLog audit, TimeProvider clock) : IOperatorDirectory
{
    public const string DefaultTimeZone = "Africa/Cairo";

    private DbTx Tx => session.Current;

    // ----------------------------------------------------------------------------------------------- directory

    public Task<OperatorRef?> FindAsync(Guid operatorId, CancellationToken ct = default) => FindAsync(Tx, operatorId, ct);

    private static async Task<OperatorRef?> FindAsync(DbTx tx, Guid operatorId, CancellationToken ct) =>
        (await tx.QuerySingleOrDefaultAsync<OperatorRow>("select id, name_ar, name_en, status from station_operators where id = @id",
            new { id = operatorId }, ct)) is { } row
            ? new OperatorRef(row.Id, row.Name_Ar, row.Name_En, row.Status == "ACTIVE")
            : null;

    public Task<bool> StationExistsAsync(Guid stationId, CancellationToken ct = default) =>
        Tx.QuerySingleAsync<bool>("select exists (select 1 from stations where id = @id)", new { id = stationId }, ct);

    // ----------------------------------------------------------------------------------------------- operators

    public async Task<Result<Page<OperatorResponse>>> ListAsync(OperatorStatus? status, string? query, string? cursor, int? limit,
        CancellationToken ct)
    {
        var size = Page<OperatorResponse>.Limit(limit);
        var rows = (await Tx.QueryAsync<OperatorRow>("""
            select * from station_operators
            where (cast(@status as text) is null or status = @status)
              and (cast(@q as text) is null or lower(legal_name) like @q or lower(name_ar) like @q
                   or lower(name_en) like @q or tax_id_key(tax_id) = tax_id_key(@raw))
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new { status = status?.ToString(), q = Like(query), raw = Sql.Clean(query), cursor = Cursors.Parse(cursor), limit = size + 1 }, ct))
            .Select(r => r.ToResponse()).ToList();
        return Page<OperatorResponse>.Of(rows, size, o => o.Id);
    }

    public async Task<Result<OperatorResponse>> GetAsync(Guid id, CancellationToken ct) =>
        await ReadAsync(id, ct) is { } row ? row.ToResponse() : Result<OperatorResponse>.NotFound("Operator");

    public async Task<Result<OperatorResponse>> CreateAsync(ZawedPrincipal actor, OperatorBody body, CancellationToken ct)
    {
        var check = new EgyptianContact.Check("");
        var draft = Draft.Of(body, null, check);
        if (check.Issues.Count > 0)
        {
            return Result<OperatorResponse>.Invalid([.. check.Issues]);
        }
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        try
        {
            await Tx.ExecuteAsync("""
                insert into station_operators (id, legal_name, name_ar, name_en, tax_id, commercial_registration,
                                               contact_name, contact_phone, contact_email, status, created_at,
                                               created_by, updated_at, updated_by)
                values (@id, @legal, @ar, @en, @tax, @cr, @contact, @phone, @email, 'ACTIVE', @now, @actor, @now,
                        @actor)
                """, draft.Params(new { id, now, actor = actor.UserId }), ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return TaxIdInUse<OperatorResponse>();
        }
        await audit.RecordAsync(Tx, Rows.Actor(actor), "OPERATOR_CREATED", "OPERATOR", id, Scope.Operator(id),
            new Dictionary<string, object?>(), ct);
        return Result<OperatorResponse>.Created((await ReadAsync(id, ct))!.ToResponse());
    }

    public async Task<Result<OperatorResponse>> UpdateAsync(ZawedPrincipal actor, Guid id, OperatorBody body, CancellationToken ct)
    {
        if (await LockOperatorAsync(id, ct) is not { } before)
        {
            return Result<OperatorResponse>.NotFound("Operator");
        }
        var check = new EgyptianContact.Check("");
        var draft = Draft.Of(body, before, check);
        if (check.Issues.Count > 0)
        {
            return Result<OperatorResponse>.Invalid([.. check.Issues]);
        }
        try
        {
            await Tx.ExecuteAsync("""
                update station_operators set legal_name = @legal, name_ar = @ar, name_en = @en, tax_id = @tax,
                       commercial_registration = @cr, contact_name = @contact, contact_phone = @phone,
                       contact_email = @email, updated_at = @now, updated_by = @actor
                where id = @id
                """, draft.Params(new { id, now = clock.GetUtcNow(), actor = actor.UserId }), ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return TaxIdInUse<OperatorResponse>();
        }
        await audit.RecordAsync(Tx, Rows.Actor(actor), "OPERATOR_UPDATED", "OPERATOR", id, Scope.Operator(id),
            new Dictionary<string, object?> { ["legalNameBefore"] = before.Legal_Name }, ct);
        return (await ReadAsync(id, ct))!.ToResponse();
    }

    /// <summary>Suspending an operator stops its devices from fueling (the device channel and the station directory refuse them).</summary>
    public async Task<Result<OperatorResponse>> SetStatusAsync(ZawedPrincipal actor, Guid id, OperatorStatus status, string reason,
        CancellationToken ct)
    {
        if (await LockOperatorAsync(id, ct) is not { } before)
        {
            return Result<OperatorResponse>.NotFound("Operator");
        }
        if (before.Status == status.ToString())
        {
            return before.ToResponse();
        }
        await Tx.ExecuteAsync("update station_operators set status = @status, updated_at = @now, updated_by = @actor where id = @id",
            new { id, status = status.ToString(), now = clock.GetUtcNow(), actor = actor.UserId }, ct);
        await audit.RecordAsync(Tx, Rows.Actor(actor), "OPERATOR_STATUS_CHANGED", "OPERATOR", id, Scope.Operator(id),
            new Dictionary<string, object?> { ["from"] = before.Status, ["to"] = status.ToString(), ["reason"] = reason }, ct);
        return (await ReadAsync(id, ct))!.ToResponse();
    }

    // ------------------------------------------------------------------------------------------------ stations

    public async Task<Result<Page<StationResponse>>> StationsAsync(Guid? operatorId, StationStatus? status, string? query, string? cursor,
        int? limit, CancellationToken ct)
    {
        var size = Page<StationResponse>.Limit(limit);
        var rows = (await Tx.QueryAsync<StationRow>($"""
            select {StationRow.Columns} from stations
            where (cast(@operator as uuid) is null or operator_id = @operator)
              and (cast(@status as text) is null or status = @status)
              and (cast(@q as text) is null or lower(name_ar) like @q or lower(name_en) like @q
                   or lower(address) like @q)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new { @operator = operatorId, status = status?.ToString(), q = Like(query), cursor = Cursors.Parse(cursor), limit = size + 1 }, ct))
            .Select(r => r.ToResponse()).ToList();
        return Page<StationResponse>.Of(rows, size, s => s.Id);
    }

    public async Task<Result<StationResponse>> StationAsync(Guid id, CancellationToken ct) =>
        await ReadStationAsync(id, ct) is { } row ? row.ToResponse() : Result<StationResponse>.NotFound("Station");

    public async Task<Result<StationResponse>> CreateStationAsync(ZawedPrincipal actor, Guid operatorId, StationBody draft, CancellationToken ct)
    {
        if (await FindAsync(operatorId, ct) is null)
        {
            return Result<StationResponse>.Fail(400, "OPERATOR_NOT_FOUND", "No such operator", "operatorId");
        }
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        if (StationParams(draft) is not { } values)
        {
            return StationParamsRefusal<StationResponse>(draft);
        }
        await Tx.ExecuteAsync("""
            insert into stations (id, operator_id, name_ar, name_en, address, latitude, longitude, time_zone,
                                  connectivity, fuel_types, status, created_at, created_by, updated_at, updated_by)
            values (@id, @operator, @ar, @en, @address, cast(@lat as numeric), cast(@lon as numeric), @zone, @connectivity,
                    cast(@fuels as text[]), 'ACTIVE', @now, @actor, @now, @actor)
            """, new
        {
            id, @operator = operatorId, values.ar, values.en, values.address, values.lat, values.lon, values.zone, values.connectivity,
            values.fuels, now, actor = actor.UserId,
        }, ct);
        await audit.RecordAsync(Tx, Rows.Actor(actor), "STATION_CREATED", "STATION", id, Scope.Operator(operatorId),
            new Dictionary<string, object?> { ["nameEn"] = Sql.Clean(draft.NameEn) }, ct);
        return Result<StationResponse>.Created((await ReadStationAsync(id, ct))!.ToResponse());
    }

    public async Task<Result<StationResponse>> UpdateStationAsync(ZawedPrincipal actor, Guid id, StationBody draft, CancellationToken ct)
    {
        if (await LockStationAsync(id, ct) is not { } before)
        {
            return Result<StationResponse>.NotFound("Station");
        }
        if (StationParams(draft) is not { } values)
        {
            return StationParamsRefusal<StationResponse>(draft);
        }
        await Tx.ExecuteAsync("""
            update stations set name_ar = @ar, name_en = @en, address = @address, latitude = cast(@lat as numeric),
                   longitude = cast(@lon as numeric), time_zone = @zone, connectivity = @connectivity,
                   fuel_types = cast(@fuels as text[]), updated_at = @now, updated_by = @actor
            where id = @id
            """, new
        {
            id, values.ar, values.en, values.address, values.lat, values.lon, values.zone, values.connectivity, values.fuels,
            now = clock.GetUtcNow(), actor = actor.UserId,
        }, ct);
        var change = new Dictionary<string, object?> { ["nameEnBefore"] = before.Name_En };
        if (before.Connectivity != draft.Connectivity!.Value.ToString())
        {
            change["connectivity"] = new Dictionary<string, object> { ["from"] = before.Connectivity, ["to"] = draft.Connectivity.Value.ToString() };
        }
        await audit.RecordAsync(Tx, Rows.Actor(actor), "STATION_UPDATED", "STATION", id, Scope.Operator(before.Operator_Id), change, ct);
        return (await ReadStationAsync(id, ct))!.ToResponse();
    }

    public async Task<Result<StationResponse>> SetStationStatusAsync(ZawedPrincipal actor, Guid id, StationStatus status, string reason,
        CancellationToken ct)
    {
        if (await LockStationAsync(id, ct) is not { } before)
        {
            return Result<StationResponse>.NotFound("Station");
        }
        if (before.StationStatus == status)
        {
            return before.ToResponse();
        }
        if (before.StationStatus == StationStatus.CLOSED)
        {
            return Result<StationResponse>.Conflict("STATION_CLOSED", "A closed station cannot be reopened");
        }
        await Tx.ExecuteAsync("update stations set status = @status, updated_at = @now, updated_by = @actor where id = @id",
            new { id, status = status.ToString(), now = clock.GetUtcNow(), actor = actor.UserId }, ct);
        await audit.RecordAsync(Tx, Rows.Actor(actor), "STATION_STATUS_CHANGED", "STATION", id, Scope.Operator(before.Operator_Id),
            new Dictionary<string, object?> { ["from"] = before.Status, ["to"] = status.ToString(), ["reason"] = reason }, ct);
        return (await ReadStationAsync(id, ct))!.ToResponse();
    }

    /// <summary>Reads the station and holds its row until commit (Java <c>lockStation</c>); null when it is not visible.</summary>
    internal Task<StationRow?> LockStationAsync(Guid id, CancellationToken ct) =>
        Tx.QuerySingleOrDefaultAsync<StationRow>($"select {StationRow.Columns} from stations where id = @id for update", new { id }, ct);

    // ------------------------------------------------------------------------------------------------- helpers

    private Task<OperatorRow?> ReadAsync(Guid id, CancellationToken ct) =>
        Tx.QuerySingleOrDefaultAsync<OperatorRow>("select * from station_operators where id = @id", new { id }, ct);

    /// <summary>Reads the operator and holds its row until commit, so two changes never both read the same "before".</summary>
    private Task<OperatorRow?> LockOperatorAsync(Guid id, CancellationToken ct) =>
        Tx.QuerySingleOrDefaultAsync<OperatorRow>("select * from station_operators where id = @id for update", new { id }, ct);

    private Task<StationRow?> ReadStationAsync(Guid id, CancellationToken ct) =>
        Tx.QuerySingleOrDefaultAsync<StationRow>($"select {StationRow.Columns} from stations where id = @id", new { id }, ct);

    private sealed record StationValues(string? ar, string? en, string? address, string? lat, string? lon, string zone, string connectivity,
        string fuels);

    /// <summary>
    /// Java <c>stationParams</c>: the zone defaults to Cairo and must be a known IANA zone (normalised to its id); both
    /// coordinates or neither; fuels sorted in the enum's order. Null when the zone or the location is refused.
    /// </summary>
    private static StationValues? StationParams(StationBody draft)
    {
        if (ZoneOf(draft.TimeZone) is not { } zone || (draft.Latitude is null) != (draft.Longitude is null))
        {
            return null;
        }
        return new StationValues(Sql.Clean(draft.NameAr), Sql.Clean(draft.NameEn), Sql.Clean(draft.Address), draft.Latitude?.Text,
            draft.Longitude?.Text, zone, draft.Connectivity!.Value.ToString(),
            "{" + string.Join(",", draft.FuelTypes!.Order().Select(f => f.ToString())) + "}");
    }

    private static Result<T> StationParamsRefusal<T>(StationBody draft) => ZoneOf(draft.TimeZone) is null
        ? Result<T>.Fail(400, "INVALID_TIME_ZONE", "Unknown time zone", "timeZone")
        : Result<T>.Fail(400, "INCOMPLETE_LOCATION", "Give both latitude and longitude", "latitude");

    /// <summary>Java <c>ZoneId.of(zone).getId()</c> of the cleaned zone, Cairo when none is given; null when refused.</summary>
    internal static string? ZoneOf(string? typed) => Zones.Normalise(Sql.Clean(typed) ?? DefaultTimeZone);

    /// <summary>A search as a LIKE pattern on lower-cased text: <c>%</c>, <c>_</c> and <c>\</c> are matched literally.</summary>
    internal static string? Like(string? query) => Sql.Clean(query) is { } cleaned
        ? "%" + cleaned.ToLowerInvariant().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%"
        : null;

    private static Result<T> TaxIdInUse<T>() => Result<T>.Fail(409, "TAX_ID_IN_USE", "Another operator has this tax id", "taxId");

    /// <summary>The body with its contact numbers cleaned and judged, against the stored operator on an update (Java <c>OperatorDraft.checked</c>).</summary>
    private sealed record Draft(string? Legal, string? Ar, string? En, string? Tax, string? Cr, string? Contact, string? Phone, string? Email)
    {
        public static Draft Of(OperatorBody body, OperatorRow? before, EgyptianContact.Check check)
        {
            var tax = check.TaxId("taxId", body.TaxId, before?.Tax_Id);
            var cr = check.CommercialRegistration("commercialRegistration", body.CommercialRegistration, before?.Commercial_Registration);
            var phone = check.Phone("contactPhone", body.ContactPhone, before?.Contact_Phone);
            return new Draft(Sql.Clean(body.LegalName), Sql.Clean(body.NameAr), Sql.Clean(body.NameEn), Sql.Clean(tax), Sql.Clean(cr),
                Sql.Clean(body.ContactName), Sql.Clean(phone), Sql.Clean(body.ContactEmail)?.ToLowerInvariant());
        }

        public Dapper.DynamicParameters Params(object extra)
        {
            var parameters = new Dapper.DynamicParameters(extra);
            parameters.AddDynamicParams(new
            {
                legal = Legal, ar = Ar, en = En, tax = Tax, cr = Cr, contact = Contact, phone = Phone, email = Email,
            });
            return parameters;
        }
    }
}
