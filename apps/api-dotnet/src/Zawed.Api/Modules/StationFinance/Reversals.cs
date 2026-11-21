using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Fueling.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.StationFinance;

public enum ReversalStatus
{
    /// <summary>Requested, waiting for a second person.</summary>
    PENDING,

    /// <summary>Approved and posted.</summary>
    APPROVED,
    REJECTED,
}

/// <summary>A reversal with the receipt reference and plate of the fueling it is about (Java <c>Reversals.Reversal</c>).</summary>
public sealed record FuelingReversal(Guid Id, Guid FuelingId, string FuelingReference, string Plate, Guid CompanyId, Guid OperatorId,
    Guid StationId, long AmountPiastres, string Reason, ReversalStatus Status, Guid RequestedBy, DateTimeOffset RequestedAt, Guid? DecidedBy,
    DateTimeOffset? DecidedAt, string? DecisionReason, Guid? LedgerTransactionId);

/// <summary>List filters. A null field does not filter.</summary>
public sealed record ReversalFilter(Guid? CompanyId, Guid? OperatorId, Guid? StationId, Guid? FuelingId, ReversalStatus? Status);

/// <summary>
/// Two-person reversals (PRD §6.4, roadmap delta A4: "keep finality"; Java <c>Reversals</c>). A completed fueling is never
/// edited, rejected or deleted. When it must be undone, one person asks with a reason and a <b>different</b> person
/// approves; approval posts a <c>REVERSAL</c> that is the exact mirror of what the fueling posted:
/// <list type="bullet">
/// <item>the company gets back what it paid: its wallet the part the wallet paid, its receivable the part that was a debt
/// (an offline fueling the wallet could not cover, B5);</item>
/// <item>the station's payable is debited the full amount. If the fueling was already paid out, the payable goes below
/// zero: the station owes the difference, and it comes out of its next payouts (<see cref="PayableBook"/>: nothing is
/// available while the balance is negative).</item>
/// </list>
/// A fueling is reversed at most once: one live request per fueling here (a unique index), one reversal per ledger
/// transaction in the ledger (V9). The fueling row does not change; the reversal is a record of its own that both
/// audiences can read once it is approved.
/// <para>What a reversal does <b>not</b> do: it does not give back the vehicle's limit usage, and it does not clear the
/// tag's cooldown. Whether it should is a product decision that has not been made.</para>
/// </summary>
public sealed class Reversals(IDbSession session, IFuelingApi fuelings, ILedger ledger, IAuditLog audit, TimeProvider clock)
{
    private const string Select = """
        select r.*, f.reference as fueling_reference, f.plate
        from fueling_reversals r join fuelings f on f.id = r.fueling_id

        """;

    private DbTx Tx => session.Current;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid Fueling_Id { get; set; }
        public string Fueling_Reference { get; set; } = "";
        public string Plate { get; set; } = "";
        public Guid Company_Id { get; set; }
        public Guid Operator_Id { get; set; }
        public Guid Station_Id { get; set; }
        public long Amount_Piastres { get; set; }
        public string Reason { get; set; } = "";
        public string Status { get; set; } = "";
        public Guid Requested_By { get; set; }
        public DateTime Requested_At { get; set; }
        public Guid? Decided_By { get; set; }
        public DateTime? Decided_At { get; set; }
        public string? Decision_Reason { get; set; }
        public Guid? Ledger_Transaction_Id { get; set; }

        public FuelingReversal ToReversal() => new(Id, Fueling_Id, Fueling_Reference, Plate, Company_Id, Operator_Id, Station_Id, Amount_Piastres,
            Reason, Enum.Parse<ReversalStatus>(Status), Requested_By, Finance.Utc(Requested_At), Decided_By, Finance.Utc(Decided_At),
            Decision_Reason, Ledger_Transaction_Id);
    }

    // ----------------------------------------------------------------------------------------------------- queries

    public async Task<Page<FuelingReversal>> ListAsync(ReversalFilter filter, string? cursor, int? limit, CancellationToken ct)
    {
        var size = Page<FuelingReversal>.Limit(limit);
        var rows = await Tx.QueryAsync<Row>(Select + """
            where (cast(@company as uuid) is null or r.company_id = @company)
              and (cast(@operator as uuid) is null or r.operator_id = @operator)
              and (cast(@station as uuid) is null or r.station_id = @station)
              and (cast(@fueling as uuid) is null or r.fueling_id = @fueling)
              and (cast(@status as text) is null or r.status = @status)
              and (cast(@cursor as uuid) is null or r.id < @cursor)
            order by r.id desc limit @limit
            """, new
        {
            company = filter.CompanyId,
            @operator = filter.OperatorId,
            station = filter.StationId,
            fueling = filter.FuelingId,
            status = filter.Status?.ToString(),
            cursor = Cursors.Parse(cursor),
            limit = size + 1,
        }, ct);
        return Page<FuelingReversal>.Of(rows.Select(r => r.ToReversal()).ToList(), size, r => r.Id);
    }

    public async Task<FuelingReversal> GetAsync(Guid id, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>(Select + "where r.id = @id", new { id }, ct))?.ToReversal()
        ?? throw ApiException.NotFound("Reversal");

    // ------------------------------------------------------------------------------------------------- two persons

    /// <summary>Step one: ask for a completed fueling to be reversed, with the reason. Nothing moves yet.</summary>
    public async Task<FuelingReversal> RequestAsync(ZawedPrincipal actor, Guid fuelingId, string reason, CancellationToken ct)
    {
        var fueling = await fuelings.FindAsync(fuelingId, ct) ?? throw ApiException.NotFound("Fueling");
        // Only a fueling that moved money can have that movement mirrored: a declined one posted nothing, and neither did
        // one that was not billed or cost nothing.
        if (fueling.Status != "COMPLETED" || fueling.LedgerTransactionId is null)
        {
            throw new ApiException(409, "FUELING_NOT_REVERSIBLE", "Only a completed fueling that was charged can be reversed");
        }
        if (await LiveStatusAsync(fuelingId, ct) is { } live)
        {
            throw ReversalExists(live);
        }
        var now = Finance.Now(clock);
        var id = Guid.CreateVersion7(now);
        try
        {
            await Tx.ExecuteAsync("""
                insert into fueling_reversals (id, fueling_id, company_id, operator_id, station_id, amount_piastres,
                                               reason, status, requested_by, requested_at, created_at, updated_at)
                values (@id, @fueling, @company, @operator, @station, @amount, @reason, 'PENDING', @actor, @now, @now,
                        @now)
                """, new
            {
                id,
                fueling = fueling.Id,
                company = fueling.CompanyId,
                @operator = fueling.OperatorId,
                station = fueling.StationId,
                amount = fueling.AmountPiastres,
                reason,
                actor = actor.UserId,
                now = now.UtcDateTime,
            }, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw ReversalExists(ReversalStatus.PENDING); // a concurrent request for the same fueling won the unique index
        }
        await audit.RecordAsync(Tx, Finance.ActorOf(actor), "REVERSAL_REQUESTED", "FUELING_REVERSAL", id, Scope.Company(fueling.CompanyId),
            Facts(fueling, reason), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Step two, by a different person: the mirror of the fueling's posting is posted, in this transaction, and the
    /// request becomes <c>APPROVED</c>.
    /// </summary>
    public async Task<FuelingReversal> ApproveAsync(ZawedPrincipal actor, Guid id, CancellationToken ct)
    {
        var reversal = await LockAsync(id, ct);
        RequirePending(reversal);
        if (reversal.RequestedBy == actor.UserId)
        {
            throw Finance.SelfApproval();
        }
        var fueling = await fuelings.FindAsync(reversal.FuelingId, ct)
            ?? throw new InvalidOperationException("Reversal " + id + " has no fueling");
        // Lock order: the request row, then (inside the ledger) the company's wallet, then the station's payable: the
        // order a fueling takes them in. The ledger builds the mirror from the fueling's own entries, so the wallet and
        // receivable parts come back exactly as they went.
        var posted = await ledger.ReverseAsync(new Reversal(id, fueling.LedgerTransactionId!.Value, fueling.Reference,
            "Reversal of fueling " + fueling.Reference, Poster.Of(actor)), ct);
        var now = Finance.Now(clock);
        var updated = await Tx.ExecuteAsync("""
            update fueling_reversals set status = 'APPROVED', decided_by = @actor, decided_at = @now,
                   ledger_transaction_id = @txn, updated_at = @now
            where id = @id and status = 'PENDING'
            """, new { actor = actor.UserId, now = now.UtcDateTime, txn = posted.TransactionId, id }, ct);
        if (updated != 1)
        {
            throw NotPending(reversal.Status);
        }
        var facts = Facts(fueling, reversal.Reason);
        facts["requestedBy"] = reversal.RequestedBy.ToString();
        facts["transactionId"] = posted.TransactionId.ToString();
        if (posted.Wallets.Count > 0)
        {
            facts["walletCreditedPiastres"] = posted.Wallets[0].BalanceAfter - posted.Wallets[0].BalanceBefore;
            facts["walletAfterPiastres"] = posted.Wallets[0].BalanceAfter;
        }
        // Negative here means the fueling had been paid out: the station now owes this much.
        facts["payableAfterPiastres"] = posted.Payables[0].BalanceAfter;
        await audit.RecordAsync(Tx, Finance.ActorOf(actor), "REVERSAL_APPROVED", "FUELING_REVERSAL", id, Scope.Company(reversal.CompanyId),
            facts, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>The request is refused, by anyone who may decide, the requester included. Nothing was posted.</summary>
    public async Task<FuelingReversal> RejectAsync(ZawedPrincipal actor, Guid id, string reason, CancellationToken ct)
    {
        var reversal = await LockAsync(id, ct);
        RequirePending(reversal);
        var now = Finance.Now(clock);
        var updated = await Tx.ExecuteAsync("""
            update fueling_reversals set status = 'REJECTED', decided_by = @actor, decided_at = @now,
                   decision_reason = @reason, updated_at = @now
            where id = @id and status = 'PENDING'
            """, new { actor = actor.UserId, now = now.UtcDateTime, reason, id }, ct);
        if (updated != 1)
        {
            throw NotPending(reversal.Status);
        }
        await audit.RecordAsync(Tx, Finance.ActorOf(actor), "REVERSAL_REJECTED", "FUELING_REVERSAL", id, Scope.Company(reversal.CompanyId),
            new Dictionary<string, object?>
            {
                ["fuelingId"] = reversal.FuelingId.ToString(),
                ["reference"] = reversal.FuelingReference,
                ["amountPiastres"] = reversal.AmountPiastres,
                ["reason"] = reason,
            }, ct);
        return await GetAsync(id, ct);
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    /// <summary>The request that is pending or approved for this fueling, if there is one.</summary>
    private async Task<ReversalStatus?> LiveStatusAsync(Guid fuelingId, CancellationToken ct) =>
        await Tx.QuerySingleOrDefaultAsync<string>(
            "select status from fueling_reversals where fueling_id = @fueling and status in ('PENDING', 'APPROVED')",
            new { fueling = fuelingId }, ct) is { } status ? Enum.Parse<ReversalStatus>(status) : null;

    /// <summary>Reads the request and holds its row until commit, so two decisions never both see PENDING.</summary>
    private async Task<FuelingReversal> LockAsync(Guid id, CancellationToken ct) =>
        // FOR UPDATE OF r: the fueling row is immutable and is not what is being decided.
        (await Tx.QuerySingleOrDefaultAsync<Row>(Select + "where r.id = @id for update of r", new { id }, ct))?.ToReversal()
        ?? throw ApiException.NotFound("Reversal");

    private static void RequirePending(FuelingReversal reversal)
    {
        if (reversal.Status != ReversalStatus.PENDING)
        {
            throw NotPending(reversal.Status);
        }
    }

    private static Dictionary<string, object?> Facts(FuelingRef fueling, string reason) => new()
    {
        ["fuelingId"] = fueling.Id.ToString(),
        ["reference"] = fueling.Reference,
        ["stationId"] = fueling.StationId.ToString(),
        ["amountPiastres"] = fueling.AmountPiastres,
        ["reason"] = reason,
    };

    private static ApiException NotPending(ReversalStatus status) => new(409, "REVERSAL_NOT_PENDING", "The request has already been decided", null,
        new Dictionary<string, object> { ["status"] = status.ToString() });

    private static ApiException ReversalExists(ReversalStatus status) => new(409, "REVERSAL_EXISTS",
        status == ReversalStatus.APPROVED ? "This fueling has already been reversed" : "A reversal of this fueling is already waiting for approval",
        null, new Dictionary<string, object> { ["status"] = status.ToString() });
}
