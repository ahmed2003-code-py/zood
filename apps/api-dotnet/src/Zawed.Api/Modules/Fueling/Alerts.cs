using System.Text.Json;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Fueling.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fueling;

/// <summary>Java <c>Alerts.Type</c>.</summary>
public enum AlertType
{
    TANK_CAPACITY_EXCEEDED,
    VIP_CEILING_PASSED,
    LOW_BALANCE,

    /// <summary>An offline fueling was recorded although a check failed (B5 contract §4).</summary>
    SYNC_CONFLICT,

    /// <summary>A device sent a capture that could not be recorded at all: fuel left the pump unaccounted.</summary>
    SYNC_REJECTED,

    /// <summary>A device spent more offline for one company than it may; the payable is held (B5 review 2).</summary>
    OFFLINE_EXPOSURE,

    /// <summary>The server completed a fueling the agent was told was refused (B5 review 6).</summary>
    OUTCOME_MISMATCH,

    /// <summary>A company's offline fuelings across all devices in 24 hours crossed the monitoring threshold (V14, ADR 0025).</summary>
    COMPANY_OFFLINE_EXPOSURE,
}

public enum AlertSeverity
{
    WARNING,
    CRITICAL,
}

public enum AlertStatus
{
    OPEN,
    ACKNOWLEDGED,
}

/// <summary>An alert row (Java <c>Alerts.Alert</c>).</summary>
public sealed record Alert(Guid Id, AlertType Type, AlertSeverity Severity, AlertStatus Status, Guid? CompanyId, Guid? OperatorId,
    Guid? StationId, Guid? DeviceId, Guid? VehicleId, Guid? FuelingId, IReadOnlyDictionary<string, JsonElement> Details, bool CompanyVisible,
    DateTimeOffset CreatedAt, Guid? AcknowledgedBy, DateTimeOffset? AcknowledgedAt);

/// <summary>Where an alert points. Null fields do not apply.</summary>
public sealed record AlertSubject(Guid? CompanyId, Guid? OperatorId, Guid? StationId, Guid? DeviceId, Guid? VehicleId, Guid? FuelingId)
{
    /// <summary>An alert about a company alone (its wallet).</summary>
    public static AlertSubject Company(Guid companyId) => new(companyId, null, null, null, null, null);
}

/// <summary>
/// Hard alerts (PRD §10; Java <c>Alerts</c>): raised in the transaction of the event that causes them, never blocking it,
/// always visible to Operations. A company sees the ones meant for it; the VIP ceiling alert is for Operations only (PRD §4.5,
/// the ceiling is invisible to the company). Raises <see cref="AlertType.LOW_BALANCE"/> on the ledger's
/// <see cref="WalletLowBalance"/>, inside the posting's transaction.
/// </summary>
public sealed class Alerts(IDbSession session, IAuditLog audit, IDomainEvents events, TimeProvider clock)
    : IDomainEventHandler<WalletLowBalance>
{
    private DbTx Tx => session.Current;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Status { get; set; } = "";
        public Guid? Company_Id { get; set; }
        public Guid? Operator_Id { get; set; }
        public Guid? Station_Id { get; set; }
        public Guid? Device_Id { get; set; }
        public Guid? Vehicle_Id { get; set; }
        public Guid? Fueling_Id { get; set; }
        public string Details { get; set; } = "{}";
        public bool Company_Visible { get; set; }
        public DateTime Created_At { get; set; }
        public Guid? Acknowledged_By { get; set; }
        public DateTime? Acknowledged_At { get; set; }

        public Alert ToAlert() => new(Id, Enum.Parse<AlertType>(Type), Enum.Parse<AlertSeverity>(Severity), Enum.Parse<AlertStatus>(Status),
            Company_Id, Operator_Id, Station_Id, Device_Id, Vehicle_Id, Fueling_Id,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Details)!, Company_Visible, Fuelings.Utc(Created_At), Acknowledged_By,
            Acknowledged_At is { } at ? Fuelings.Utc(at) : null);
    }

    /// <summary>The unique index that keeps one OUTCOME_MISMATCH alert per fueling (V8_1).</summary>
    public const string OutcomeMismatchOnce = "alerts_outcome_mismatch_once";

    /// <summary>Raises an alert in the caller's transaction, which must run in the platform scope.</summary>
    public async Task RaiseAsync(AlertType type, AlertSeverity severity, AlertSubject subject, IReadOnlyDictionary<string, object?> details,
        bool companyVisible, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        // ON CONFLICT names the one rule the database keeps on alerts (V8, alerts_sync_rejected_once): a rejected sync item is
        // reported once per device, clientTxnId and code, however often the device sends it again. No other alert can match
        // it, so every other alert is always inserted.
        var inserted = await Tx.ExecuteAsync("""
            insert into alerts (id, type, severity, status, company_id, operator_id, station_id, device_id, vehicle_id,
                                fueling_id, details, company_visible, created_at)
            values (@id, @type, @severity, 'OPEN', @company, @operator, @station, @device, @vehicle, @fueling,
                    cast(@details as jsonb), @visible, @now)
            on conflict (device_id, (details ->> 'clientTxnId'), (details ->> 'code')) where type = 'SYNC_REJECTED'
            do nothing
            """, new
        {
            id,
            type = type.ToString(),
            severity = severity.ToString(),
            company = subject.CompanyId,
            @operator = subject.OperatorId,
            station = subject.StationId,
            device = subject.DeviceId,
            vehicle = subject.VehicleId,
            fueling = subject.FuelingId,
            details = JsonSerializer.Serialize(details),
            visible = companyVisible,
            now = now.UtcDateTime,
        }, ct);
        // Only an alert that was written is announced: a repeated sync rejection was told the first time.
        if (inserted == 1)
        {
            await events.PublishAsync(new HardAlertRaised(id, type.ToString(), severity.ToString(), subject.CompanyId, subject.OperatorId,
                subject.StationId, subject.FuelingId), ct);
        }
    }

    /// <summary>Whether an alert of this type already points at this fueling.</summary>
    public Task<bool> ExistsAsync(AlertType type, Guid fuelingId, CancellationToken ct) =>
        Tx.QuerySingleAsync<bool>("select exists (select 1 from alerts where type = @type and fueling_id = @fueling)",
            new { type = type.ToString(), fueling = fuelingId }, ct);

    /// <summary>
    /// PRD §4.7: a posting took the wallet below its threshold. Runs inside the posting transaction (a fueling under the
    /// engine's platform scope, or a Super Admin adjustment), so the alert commits or rolls back with the money.
    /// </summary>
    public Task HandleAsync(WalletLowBalance domainEvent, CancellationToken ct) =>
        RaiseAsync(AlertType.LOW_BALANCE, AlertSeverity.WARNING, AlertSubject.Company(domainEvent.CompanyId), new Dictionary<string, object?>
        {
            ["balancePiastres"] = domainEvent.BalancePiastres,
            ["thresholdPiastres"] = domainEvent.ThresholdPiastres,
            ["transactionId"] = domainEvent.TransactionId.ToString(),
        }, true, ct);

    // ------------------------------------------------------------------------------------------------------ reads

    public async Task<Page<Alert>> ListAsync(Guid? companyId, AlertStatus? status, AlertType? type, string? cursor, int? limit,
        CancellationToken ct)
    {
        var size = Page<Alert>.Limit(limit);
        var rows = await Tx.QueryAsync<Row>("""
            select * from alerts
            where (cast(@company as uuid) is null or company_id = @company)
              and (cast(@status as text) is null or status = @status)
              and (cast(@type as text) is null or type = @type)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new
        {
            company = companyId,
            status = status?.ToString(),
            type = type?.ToString(),
            cursor = Cursors.Parse(cursor),
            limit = size + 1,
        }, ct);
        return Page<Alert>.Of(rows.Select(r => r.ToAlert()).ToList(), size, a => a.Id);
    }

    /// <summary>Operations has seen the alert. Acknowledging twice is a no-op.</summary>
    public async Task<Result<Alert>> AcknowledgeAsync(ZawedPrincipal actor, Guid id, CancellationToken ct)
    {
        var tx = Tx;
        if ((await tx.QuerySingleOrDefaultAsync<Row>("select * from alerts where id = @id for update", new { id }, ct))?.ToAlert() is not { } alert)
        {
            return Result<Alert>.NotFound("Alert");
        }
        if (alert.Status == AlertStatus.ACKNOWLEDGED)
        {
            return alert;
        }
        if (!actor.Scope.IsPlatform)
        {
            return Result<Alert>.Forbidden();
        }
        await tx.ExecuteAsync("update alerts set status = 'ACKNOWLEDGED', acknowledged_by = @actor, acknowledged_at = @now where id = @id",
            new { id, actor = actor.UserId, now = clock.GetUtcNow().UtcDateTime }, ct);
        await audit.RecordAsync(tx, new AuditActor(actor.UserId, actor.Role), "ALERT_ACKNOWLEDGED", "ALERT", id,
            alert.CompanyId is { } company ? Scope.Company(company) : Scope.Platform,
            new Dictionary<string, object?> { ["type"] = alert.Type.ToString() }, ct);
        return (await tx.QuerySingleAsync<Row>("select * from alerts where id = @id", new { id }, ct)).ToAlert();
    }
}
