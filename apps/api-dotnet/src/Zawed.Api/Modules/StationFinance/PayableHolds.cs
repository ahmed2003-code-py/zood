using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Fueling.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.StationFinance;

public enum HoldState
{
    /// <summary>Excluded from what may be paid out.</summary>
    HELD,

    /// <summary>Operations released it; it counts as available.</summary>
    RELEASED,

    /// <summary>The fueling was reversed; there is nothing left to hold.</summary>
    REVERSED,
}

/// <summary>A fueling whose payable was held at sync, with what became of the hold (Java <c>PayableHolds.HeldPayable</c>).</summary>
public sealed record HeldPayable(Guid FuelingId, string Reference, string Plate, Guid StationId, Guid OperatorId, long AmountPiastres,
    DateTimeOffset EvaluatedAt, DateTimeOffset ReceivedAt, IReadOnlyList<string> Conflicts, HoldState State, Guid? ReleasedBy,
    DateTimeOffset? ReleasedAt, string? ReleaseReason);

/// <summary>
/// Held payables (B5 contract §4; Java <c>PayableHolds</c>): an offline fueling recorded past the device's offline bound,
/// or at a station that was not open, credits the station in full but its amount waits for Operations before any
/// payout. Operations releases it here, with a reason.
/// <para>A fueling row never changes (V7_1), so a release is a row of its own, written once (<c>payable_hold_releases</c>,
/// append only, one per fueling by a unique index). The moment it commits, the amount counts as available
/// (<see cref="PayableBook"/>). A held fueling that was reversed is no longer held either: its payable was taken back.</para>
/// </summary>
public sealed class PayableHolds(IDbSession session, IFuelingApi fuelings, IAuditLog audit, TimeProvider clock)
{
    /// <summary>Held fuelings with the state of their hold. Only fuelings that posted: a held fueling with no amount holds nothing.</summary>
    private const string Held = """
        select f.id, f.reference, f.plate, f.station_id, f.operator_id, f.amount_piastres, f.evaluated_at,
               f.received_at, f.conflicts, h.released_by, h.released_at, h.reason as release_reason,
               case when exists (select 1 from fueling_reversals r where r.fueling_id = f.id and r.status = 'APPROVED')
                      then 'REVERSED'
                    when h.id is not null then 'RELEASED'
                    else 'HELD' end as state
        from fuelings f left join payable_hold_releases h on h.fueling_id = f.id
        where f.payable_held and f.ledger_transaction_id is not null

        """;

    private DbTx Tx => session.Current;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Reference { get; set; } = "";
        public string Plate { get; set; } = "";
        public Guid Station_Id { get; set; }
        public Guid Operator_Id { get; set; }
        public long Amount_Piastres { get; set; }
        public DateTime Evaluated_At { get; set; }
        public DateTime Received_At { get; set; }
        public string[] Conflicts { get; set; } = [];
        public Guid? Released_By { get; set; }
        public DateTime? Released_At { get; set; }
        public string? Release_Reason { get; set; }
        public string State { get; set; } = "";

        public HeldPayable ToHeld() => new(Id, Reference, Plate, Station_Id, Operator_Id, Amount_Piastres, Finance.Utc(Evaluated_At),
            Finance.Utc(Received_At), Conflicts, Enum.Parse<HoldState>(State), Released_By, Finance.Utc(Released_At), Release_Reason);
    }

    /// <summary>Held fuelings, newest first; a null <paramref name="state"/> lists them all.</summary>
    public async Task<Page<HeldPayable>> ListAsync(Guid? operatorId, Guid? stationId, HoldState? state, string? cursor, int? limit,
        CancellationToken ct)
    {
        var size = Page<HeldPayable>.Limit(limit);
        var rows = await Tx.QueryAsync<Row>("select * from (" + Held + """
            ) held
            where (cast(@operator as uuid) is null or operator_id = @operator)
              and (cast(@station as uuid) is null or station_id = @station)
              and (cast(@state as text) is null or state = @state)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new
        {
            @operator = operatorId,
            station = stationId,
            state = state?.ToString(),
            cursor = Cursors.Parse(cursor),
            limit = size + 1,
        }, ct);
        return Page<HeldPayable>.Of(rows.Select(r => r.ToHeld()).ToList(), size, h => h.FuelingId);
    }

    /// <summary>Releases the hold on one fueling's payable. Once: a release is never undone or repeated.</summary>
    public async Task<HeldPayable> ReleaseAsync(ZawedPrincipal actor, Guid fuelingId, string reason, CancellationToken ct)
    {
        // A fueling that was never held (or does not exist, or posted nothing) has no hold to release.
        var held = await FindAsync(fuelingId, ct) ?? throw (await fuelings.VisibleAsync(fuelingId, ct)
            ? new ApiException(409, "FUELING_NOT_HELD", "This fueling's payable is not held")
            : ApiException.NotFound("Fueling"));
        if (held.State == HoldState.RELEASED)
        {
            throw AlreadyReleased();
        }
        if (held.State == HoldState.REVERSED)
        {
            throw new ApiException(409, "FUELING_REVERSED", "This fueling was reversed; its payable was taken back and is no longer held");
        }
        var now = Finance.Now(clock);
        var id = Guid.CreateVersion7(now);
        try
        {
            await Tx.ExecuteAsync("""
                insert into payable_hold_releases (id, fueling_id, station_id, operator_id, amount_piastres, reason,
                                                   released_by, released_at)
                values (@id, @fueling, @station, @operator, @amount, @reason, @actor, @now)
                """, new
            {
                id,
                fueling = fuelingId,
                station = held.StationId,
                @operator = held.OperatorId,
                amount = held.AmountPiastres,
                reason,
                actor = actor.UserId,
                now = now.UtcDateTime,
            }, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw AlreadyReleased(); // a concurrent release of the same fueling won the unique index
        }
        await audit.RecordAsync(Tx, Finance.ActorOf(actor), "PAYABLE_HOLD_RELEASED", "FUELING", fuelingId, Scope.Operator(held.OperatorId),
            new Dictionary<string, object?>
            {
                ["stationId"] = held.StationId.ToString(),
                ["reference"] = held.Reference,
                ["amountPiastres"] = held.AmountPiastres,
                ["reason"] = reason,
            }, ct);
        return (await FindAsync(fuelingId, ct))!;
    }

    private async Task<HeldPayable?> FindAsync(Guid fuelingId, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>(Held + " and f.id = @id", new { id = fuelingId }, ct))?.ToHeld();

    private static ApiException AlreadyReleased() => new(409, "HOLD_ALREADY_RELEASED", "This held payable was already released");
}
