using Zawed.Api.Modules.Fueling.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;
using Zawed.Domain;
using Zawed.Domain.Rules;

namespace Zawed.Api.Modules.Fueling;

/// <summary>Java <c>Fuelings.Status</c>.</summary>
public enum FuelingStatus
{
    COMPLETED,
    DECLINED,
}

/// <summary>Java <c>Fuelings.Origin</c>.</summary>
public enum FuelingOrigin
{
    ONLINE,
    OFFLINE,
}

/// <summary>
/// What an offline fueling's checks found (B5 contract §4; Java <c>Fuelings.Conflict</c>): the reasons an online fueling
/// is declined for, which offline are recorded and alerted instead, and the offline-only findings.
/// </summary>
public enum Conflict
{
    COOLDOWN,
    TAG_INACTIVE,
    VEHICLE_INACTIVE,
    COMPANY_FROZEN,
    FUEL_TYPE_MISMATCH,
    FUEL_NOT_AVAILABLE,
    LIMIT_EXCEEDED,
    INSUFFICIENT_BALANCE,

    /// <summary>Captured more than seven days before the server received it.</summary>
    STALE_CAPTURE,

    /// <summary>The agent belongs to the station but no session of theirs on this device covers the time.</summary>
    AGENT_UNVERIFIED,

    /// <summary>The device dated it ahead of server time; judged at the time it arrived instead.</summary>
    DEVICE_CLOCK_SKEW,

    /// <summary>The same capture was declined online; its answer was lost and the terminal let the fuel go.</summary>
    DECLINED_ONLINE,

    /// <summary>The station (or its operator) was not open for fuelings.</summary>
    STATION_NOT_ACTIVE,

    /// <summary>The price the terminal showed differs from the price in force; the server's is charged.</summary>
    PRICE_MISMATCH,

    /// <summary>The device went past what it may spend offline for this company; the payable is held.</summary>
    OVER_OFFLINE_ALLOWANCE,
}

/// <summary>A fueling row (Java <c>Fuelings.Fueling</c>). Written once by the engine and never updated.</summary>
/// <param name="Conflicts">empty for an online fueling; for an offline one, every check that failed, in the order of the checks</param>
public sealed record Fueling(Guid Id, Guid ClientTxnId, string Reference, FuelingStatus Status, FuelingRules.DeclineReason? DeclineReason,
    IReadOnlyList<Conflict> Conflicts, FuelingOrigin Origin, Guid CompanyId, Guid VehicleId, Guid TagId, Guid OperatorId, Guid StationId,
    Guid DeviceId, Guid AgentId, FuelType FuelType, long VolumeMl, long UnitPricePiastres, long AmountPiastres, bool Billable,
    DateTimeOffset EvaluatedAt, DateTimeOffset DeviceAt, DateTimeOffset ReceivedAt, string ReceiptLocale, string Plate, string CompanyNameAr,
    string CompanyNameEn, string StationNameAr, string StationNameEn, string AgentWorkerId, Guid? LedgerTransactionId, bool PayableHeld)
{
    public bool Completed => Status == FuelingStatus.COMPLETED;
}

/// <summary>List filters. A null field does not filter.</summary>
public sealed record FuelingFilter(Guid? CompanyId, Guid? OperatorId, Guid? StationId, Guid? VehicleId, FuelingStatus? Status,
    DateTimeOffset? From, DateTimeOffset? To);

/// <summary>
/// Fueling records (plan §3.4; Java <c>Fuelings</c>), with the Java statements, on the caller's transaction. A row is written
/// once by the engine and never updated; the runtime role has no UPDATE or DELETE on the table. Reads run in the caller's
/// scope: row-level security shows a company its own fuelings and an operator the fuelings at its stations.
/// </summary>
public sealed class Fuelings(IDbSession session) : IFuelingApi
{
    private DbTx Tx => session.Current;

    internal sealed class Row
    {
        public Guid Id { get; set; }
        public Guid Client_Txn_Id { get; set; }
        public string Reference { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Decline_Reason { get; set; }
        public string[] Conflicts { get; set; } = [];
        public string Origin { get; set; } = "";
        public Guid Company_Id { get; set; }
        public Guid Vehicle_Id { get; set; }
        public Guid Tag_Id { get; set; }
        public Guid Operator_Id { get; set; }
        public Guid Station_Id { get; set; }
        public Guid Device_Id { get; set; }
        public Guid Agent_Id { get; set; }
        public string Fuel_Type { get; set; } = "";
        public long Volume_Ml { get; set; }
        public long Unit_Price_Piastres { get; set; }
        public long Amount_Piastres { get; set; }
        public bool Billable { get; set; }
        public DateTime Evaluated_At { get; set; }
        public DateTime Device_At { get; set; }
        public DateTime Received_At { get; set; }
        public string Receipt_Locale { get; set; } = "";
        public string Plate { get; set; } = "";
        public string Company_Name_Ar { get; set; } = "";
        public string Company_Name_En { get; set; } = "";
        public string Station_Name_Ar { get; set; } = "";
        public string Station_Name_En { get; set; } = "";
        public string Agent_Worker_Id { get; set; } = "";
        public Guid? Ledger_Transaction_Id { get; set; }
        public bool Payable_Held { get; set; }

        public Fueling ToFueling() => new(Id, Client_Txn_Id, Reference, Enum.Parse<FuelingStatus>(Status),
            Decline_Reason is null ? null : Enum.Parse<FuelingRules.DeclineReason>(Decline_Reason),
            Conflicts.Select(Enum.Parse<Conflict>).ToList().AsReadOnly(), Enum.Parse<FuelingOrigin>(Origin), Company_Id, Vehicle_Id, Tag_Id,
            Operator_Id, Station_Id, Device_Id, Agent_Id, Enum.Parse<FuelType>(Fuel_Type), Volume_Ml, Unit_Price_Piastres, Amount_Piastres,
            Billable, Utc(Evaluated_At), Utc(Device_At), Utc(Received_At), Receipt_Locale, Plate, Company_Name_Ar, Company_Name_En,
            Station_Name_Ar, Station_Name_En, Agent_Worker_Id, Ledger_Transaction_Id, Payable_Held);
    }

    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    internal static DateTime Ts(DateTimeOffset value) => value.UtcDateTime;

    internal static DateTime? Ts(DateTimeOffset? value) => value?.UtcDateTime;

    /// <summary>Short, sequential, printed on the receipt: <c>F-10001</c>.</summary>
    public async Task<string> NextReferenceAsync(CancellationToken ct) =>
        "F-" + await Tx.QuerySingleAsync<long>("select nextval('fueling_reference_seq')", null, ct);

    public async Task<Fueling?> ByClientTxnIdAsync(Guid clientTxnId, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("select * from fuelings where client_txn_id = @id", new { id = clientTxnId }, ct))?.ToFueling();

    public async Task<Fueling?> FindFuelingAsync(Guid id, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("select * from fuelings where id = @id", new { id }, ct))?.ToFueling();

    /// <summary>
    /// What the vehicle used in completed fuelings evaluated in <c>[from, to)</c>, in millilitres or piastres. Runs under the
    /// engine's wallet lock, so no fueling of the same company can land in between.
    /// </summary>
    public Task<long> VolumeInAsync(Guid vehicleId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        UsageAsync("volume_ml", vehicleId, from, to, ct);

    public Task<long> AmountInAsync(Guid vehicleId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        UsageAsync("amount_piastres", vehicleId, from, to, ct);

    private Task<long> UsageAsync(string column, Guid vehicleId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Tx.QuerySingleAsync<long>("select coalesce(sum(" + column + "), 0)::bigint from fuelings "
            + "where vehicle_id = @vehicle and status = 'COMPLETED' and evaluated_at >= @from and evaluated_at < @to",
            new { vehicle = vehicleId, from = Ts(from), to = Ts(to) }, ct);

    /// <summary>
    /// The latest completed fueling of the vehicle evaluated strictly between <paramref name="after"/> and
    /// <paramref name="before"/>, or null (Java <c>Fuelings.latestCompletedBetween</c>): the offline cooldown check.
    /// </summary>
    public async Task<DateTimeOffset?> LatestCompletedBetweenAsync(Guid vehicleId, DateTimeOffset after, DateTimeOffset before, CancellationToken ct)
    {
        var latest = await Tx.QuerySingleAsync<DateTime?>("""
            select max(evaluated_at) from fuelings
            where vehicle_id = @vehicle and status = 'COMPLETED' and evaluated_at > @after and evaluated_at < @before
            """, new { vehicle = vehicleId, after = Ts(after), before = Ts(before) }, ct);
        return latest is { } at ? Utc(at) : null;
    }

    /// <summary>
    /// What one device recorded offline for one company with a device time after <paramref name="since"/>: the exposure the
    /// offline bound limits (B5 review 2; Java <c>Fuelings.offlineAmountSince</c>).
    /// </summary>
    public Task<long> OfflineAmountSinceAsync(Guid deviceId, Guid companyId, DateTimeOffset since, CancellationToken ct) =>
        Tx.QuerySingleAsync<long>("""
            select coalesce(sum(amount_piastres), 0)::bigint from fuelings
            where device_id = @device and company_id = @company and origin = 'OFFLINE' and evaluated_at > @since
            """, new { device = deviceId, company = companyId, since = Ts(since) }, ct);

    /// <summary>A company's offline fuelings across every device since <paramref name="since"/> (V14 monitoring).</summary>
    public Task<long> CompanyOfflineAmountSinceAsync(Guid companyId, DateTimeOffset since, CancellationToken ct) =>
        Tx.QuerySingleAsync<long>("""
            select coalesce(sum(amount_piastres), 0)::bigint from fuelings
            where company_id = @company and origin = 'OFFLINE' and evaluated_at > @since
            """, new { company = companyId, since = Ts(since) }, ct);

    public Task InsertAsync(Fueling f, CancellationToken ct) => Tx.ExecuteAsync("""
        insert into fuelings (id, client_txn_id, reference, status, decline_reason, conflicts, origin, company_id, vehicle_id,
                              tag_id, operator_id, station_id, device_id, agent_id, fuel_type, volume_ml,
                              unit_price_piastres, amount_piastres, billable, evaluated_at, device_at, received_at,
                              receipt_locale, plate, company_name_ar, company_name_en, station_name_ar,
                              station_name_en, agent_worker_id, ledger_transaction_id, payable_held)
        values (@id, @clientTxnId, @reference, @status, @declineReason, cast(@conflicts as text[]), @origin, @company, @vehicle, @tag, @operator,
                @station, @device, @agent, @fuel, @volume, @unitPrice, @amount, @billable, @evaluatedAt, @deviceAt,
                @receivedAt, @locale, @plate, @companyAr, @companyEn, @stationAr, @stationEn, @worker, @ledgerTxn, @held)
        """, new
    {
        id = f.Id,
        clientTxnId = f.ClientTxnId,
        reference = f.Reference,
        status = f.Status.ToString(),
        declineReason = f.DeclineReason?.ToString(),
        // A PostgreSQL array literal: the names are enum constants, so nothing needs quoting.
        conflicts = "{" + string.Join(",", f.Conflicts.Select(c => c.ToString())) + "}",
        origin = f.Origin.ToString(),
        company = f.CompanyId,
        vehicle = f.VehicleId,
        tag = f.TagId,
        @operator = f.OperatorId,
        station = f.StationId,
        device = f.DeviceId,
        agent = f.AgentId,
        fuel = f.FuelType.ToString(),
        volume = f.VolumeMl,
        unitPrice = f.UnitPricePiastres,
        amount = f.AmountPiastres,
        billable = f.Billable,
        evaluatedAt = Ts(f.EvaluatedAt),
        deviceAt = Ts(f.DeviceAt),
        receivedAt = Ts(f.ReceivedAt),
        locale = f.ReceiptLocale,
        plate = f.Plate,
        companyAr = f.CompanyNameAr,
        companyEn = f.CompanyNameEn,
        stationAr = f.StationNameAr,
        stationEn = f.StationNameEn,
        worker = f.AgentWorkerId,
        ledgerTxn = f.LedgerTransactionId,
        held = f.PayableHeld,
    }, ct);

    // ------------------------------------------------------------------------------------------------------ reads

    public async Task<Page<Fueling>> ListAsync(FuelingFilter filter, string? cursor, int? limit, CancellationToken ct)
    {
        var size = Page<Fueling>.Limit(limit);
        var rows = await Tx.QueryAsync<Row>("""
            select * from fuelings
            where (cast(@company as uuid) is null or company_id = @company)
              and (cast(@operator as uuid) is null or operator_id = @operator)
              and (cast(@station as uuid) is null or station_id = @station)
              and (cast(@vehicle as uuid) is null or vehicle_id = @vehicle)
              and (cast(@status as text) is null or status = @status)
              and (cast(@from as timestamptz) is null or evaluated_at >= @from)
              and (cast(@to as timestamptz) is null or evaluated_at < @to)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new
        {
            company = filter.CompanyId,
            @operator = filter.OperatorId,
            station = filter.StationId,
            vehicle = filter.VehicleId,
            status = filter.Status?.ToString(),
            from = Ts(filter.From),
            to = Ts(filter.To),
            cursor = Cursors.Parse(cursor),
            limit = size + 1,
        }, ct);
        return Page<Fueling>.Of(rows.Select(r => r.ToFueling()).ToList(), size, f => f.Id);
    }

    // ------------------------------------------------------------------------------------------------- IFuelingApi

    public Task<bool> VisibleAsync(Guid fuelingId, CancellationToken ct = default) =>
        Tx.QuerySingleAsync<bool>("select exists (select 1 from fuelings where id = @id)", new { id = fuelingId }, ct);

    public async Task<FuelingRef?> FindAsync(Guid fuelingId, CancellationToken ct = default) =>
        await FindFuelingAsync(fuelingId, ct) is { } f
            ? new FuelingRef(f.Id, f.Reference, f.Status.ToString(), f.Origin.ToString(), f.CompanyId, f.VehicleId, f.OperatorId, f.StationId,
                f.DeviceId, f.FuelType, f.VolumeMl, f.AmountPiastres, f.Billable, f.EvaluatedAt, f.LedgerTransactionId, f.PayableHeld)
            : null;

    private sealed class UsageRow
    {
        public Guid Vehicle_Id { get; set; }
        public long Day_Ml { get; set; }
        public long Week_Ml { get; set; }
        public long Month_Ml { get; set; }
        public long Day_P { get; set; }
        public long Week_P { get; set; }
        public long Month_P { get; set; }
    }

    /// <summary>Java <c>FuelingUsageReader.usageSince</c>, with its statement.</summary>
    public async Task<IReadOnlyDictionary<Guid, FuelingUsage>> UsageSinceAsync(DateTimeOffset dayStart, DateTimeOffset weekStart,
        DateTimeOffset monthStart, CancellationToken ct = default)
    {
        var earliest = weekStart < monthStart ? weekStart : monthStart;
        var rows = await Tx.QueryAsync<UsageRow>("""
            select vehicle_id,
                   coalesce(sum(volume_ml) filter (where evaluated_at >= @day), 0)::bigint as day_ml,
                   coalesce(sum(volume_ml) filter (where evaluated_at >= @week), 0)::bigint as week_ml,
                   coalesce(sum(volume_ml) filter (where evaluated_at >= @month), 0)::bigint as month_ml,
                   coalesce(sum(amount_piastres) filter (where evaluated_at >= @day), 0)::bigint as day_p,
                   coalesce(sum(amount_piastres) filter (where evaluated_at >= @week), 0)::bigint as week_p,
                   coalesce(sum(amount_piastres) filter (where evaluated_at >= @month), 0)::bigint as month_p
            from fuelings
            where status = 'COMPLETED' and evaluated_at >= @earliest
            group by vehicle_id
            """, new
        {
            day = Ts(dayStart),
            week = Ts(weekStart),
            month = Ts(monthStart),
            earliest = Ts(earliest < dayStart ? earliest : dayStart),
        }, ct);
        return rows.ToDictionary(r => r.Vehicle_Id, r => new FuelingUsage(r.Day_Ml, r.Week_Ml, r.Month_Ml, r.Day_P, r.Week_P, r.Month_P));
    }
}
