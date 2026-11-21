using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Ledger;

/// <summary>A wallet adjustment as stored (Java <c>WalletAdjustments.Adjustment</c>).</summary>
public sealed record Adjustment(Guid Id, Guid CompanyId, long AmountPiastres, string Reason, AdjustmentStatus Status, Guid RequestedBy,
    DateTimeOffset RequestedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionReason, Guid? LedgerTransactionId);

/// <summary>
/// Wallet credits that are not bank transfers (V12, ADR 0021; Java <c>WalletAdjustments</c>): a goodwill credit, a
/// correction. They are the exception, so they are never a shortcut: one Operations user asks with a reason, a
/// <b>different</b> user holding <c>TOPUP_APPROVE</c> approves, and only then does the ledger post
/// <c>platform:adjustments</c> → the wallet. The adjustment account keeps them apart from real bank money. Credits only:
/// taking money out of a wallet is a reversal of what put it there.
/// </summary>
public sealed class WalletAdjustments(IDbSession session, ILedger ledger, IAuditLog audit, TimeProvider clock, IdempotencyKeys keys,
    IDomainEvents events)
{
    public const string Operation = "wallet_adjustment.create";

    private DbTx Tx => session.Current;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid Company_Id { get; set; }
        public long Amount_Piastres { get; set; }
        public string Reason { get; set; } = "";
        public string Status { get; set; } = "";
        public Guid Requested_By { get; set; }
        public DateTime Requested_At { get; set; }
        public Guid? Decided_By { get; set; }
        public DateTime? Decided_At { get; set; }
        public string? Decision_Reason { get; set; }
        public Guid? Ledger_Transaction_Id { get; set; }

        public Adjustment ToAdjustment() => new(Id, Company_Id, Amount_Piastres, Reason, Enum.Parse<AdjustmentStatus>(Status), Requested_By,
            LedgerTime.Utc(Requested_At), Decided_By, LedgerTime.Utc(Decided_At), Decision_Reason, Ledger_Transaction_Id);
    }

    public async Task<Page<Adjustment>> ListAsync(Guid? companyId, AdjustmentStatus? status, string? cursor, int? limit, CancellationToken ct)
    {
        var size = Page<Adjustment>.Limit(limit);
        var rows = await Tx.QueryAsync<Row>("""
            select * from wallet_adjustments
            where (cast(@company as uuid) is null or company_id = @company)
              and (cast(@status as text) is null or status = @status)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new { company = companyId, status = status?.ToString(), cursor = Cursors.Parse(cursor), limit = size + 1 }, ct);
        return Page<Adjustment>.Of(rows.Select(r => r.ToAdjustment()).ToList(), size, a => a.Id);
    }

    public async Task<Adjustment> GetAsync(Guid id, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("select * from wallet_adjustments where id = @id", new { id }, ct))?.ToAdjustment()
        ?? throw ApiException.NotFound("Adjustment");

    /// <summary>
    /// Asks for a credit. Nothing moves until a different person approves. The Idempotency-Key is mandatory (ADR 0022):
    /// an adjustment has no natural key, so without one a retried request would be a second pending adjustment that an
    /// approver could approve as well, crediting the wallet twice. A repeat with the same key answers the adjustment it
    /// created, and publishes nothing.
    /// </summary>
    /// <param name="body">the request body, hashed with the company id as Java hashed <c>List.of(companyId, body)</c></param>
    public async Task<Adjustment> RequestAsync(ZawedPrincipal actor, Guid companyId, string? idempotencyKey, object body, long amountPiastres,
        string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        if (await keys.ClaimRequiredAsync(actor.UserId, Operation, idempotencyKey, new object[] { companyId, body }, id, ct) is { } earlier)
        {
            return await GetAsync(earlier, ct);
        }
        if (await Tx.QuerySingleOrDefaultAsync<int?>("select 1 from company_wallets where company_id = @company", new { company = companyId }, ct) is null)
        {
            throw ApiException.NotFound("Wallet");
        }
        var stripped = JavaText.Strip(reason);
        await Tx.ExecuteAsync("""
            insert into wallet_adjustments (id, company_id, amount_piastres, reason, status, requested_by, requested_at,
                                            created_at, updated_at)
            values (@id, @company, @amount, @reason, 'PENDING_APPROVAL', @actor, @now, @now, @now)
            """, new { id, company = companyId, amount = amountPiastres, reason = stripped, actor = actor.UserId, now = now.UtcDateTime }, ct);
        await audit.RecordAsync(Tx, TopUps.ActorOf(actor), "WALLET_ADJUSTMENT_REQUESTED", "WALLET_ADJUSTMENT", id, Scope.Company(companyId),
            new Dictionary<string, object?> { ["amountPiastres"] = amountPiastres, ["reason"] = stripped }, ct);
        await events.PublishAsync(new WalletAdjustmentAwaitingApproval(id, companyId, amountPiastres), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>A different person approves: the wallet is credited once. A repeat answers it as it is.</summary>
    public async Task<Adjustment> ApproveAsync(ZawedPrincipal actor, Guid id, CancellationToken ct)
    {
        var adjustment = await LockAsync(id, ct);
        if (adjustment.Status == AdjustmentStatus.APPROVED)
        {
            return adjustment;
        }
        RequirePending(adjustment);
        if (actor.UserId == adjustment.RequestedBy)
        {
            throw new ApiException(403, "SELF_APPROVAL_NOT_ALLOWED", "The person who asked for an adjustment cannot approve it");
        }
        await ledger.LockWalletAsync(adjustment.CompanyId, ct);
        var posted = await ledger.PostAsync(new Posting(SourceType.ADJUSTMENT, id, "ADJ-" + id.ToString()[..8],
            "Wallet adjustment: " + adjustment.Reason, Poster.Of(actor),
            [Entry.Debit(Account.PlatformAdjustments(), adjustment.AmountPiastres), Entry.Credit(Account.CompanyWallet(adjustment.CompanyId), adjustment.AmountPiastres)]),
            ct);
        await DecideAsync(id, AdjustmentStatus.APPROVED, actor, null, posted.TransactionId, ct);
        await audit.RecordAsync(Tx, TopUps.ActorOf(actor), "WALLET_ADJUSTMENT_APPROVED", "WALLET_ADJUSTMENT", id, Scope.Company(adjustment.CompanyId),
            new Dictionary<string, object?>
            {
                ["amountPiastres"] = adjustment.AmountPiastres,
                ["requestedBy"] = adjustment.RequestedBy.ToString(),
                ["transactionId"] = posted.TransactionId.ToString(),
                ["balanceAfterPiastres"] = posted.Wallets[0].BalanceAfter,
            }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<Adjustment> RejectAsync(ZawedPrincipal actor, Guid id, string reason, CancellationToken ct)
    {
        var adjustment = await LockAsync(id, ct);
        RequirePending(adjustment);
        await DecideAsync(id, AdjustmentStatus.REJECTED, actor, reason, null, ct);
        await audit.RecordAsync(Tx, TopUps.ActorOf(actor), "WALLET_ADJUSTMENT_REJECTED", "WALLET_ADJUSTMENT", id, Scope.Company(adjustment.CompanyId),
            new Dictionary<string, object?> { ["amountPiastres"] = adjustment.AmountPiastres, ["reason"] = reason }, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>The requester withdraws an adjustment nobody has decided.</summary>
    public async Task<Adjustment> CancelAsync(ZawedPrincipal actor, Guid id, CancellationToken ct)
    {
        var adjustment = await LockAsync(id, ct);
        RequirePending(adjustment);
        if (actor.UserId != adjustment.RequestedBy)
        {
            throw new ApiException(403, "FORBIDDEN", "Only the requester withdraws an adjustment");
        }
        await DecideAsync(id, AdjustmentStatus.CANCELLED, actor, null, null, ct);
        await audit.RecordAsync(Tx, TopUps.ActorOf(actor), "WALLET_ADJUSTMENT_CANCELLED", "WALLET_ADJUSTMENT", id, Scope.Company(adjustment.CompanyId),
            new Dictionary<string, object?> { ["amountPiastres"] = adjustment.AmountPiastres }, ct);
        return await GetAsync(id, ct);
    }

    private async Task<Adjustment> LockAsync(Guid id, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("select * from wallet_adjustments where id = @id for update", new { id }, ct))?.ToAdjustment()
        ?? throw ApiException.NotFound("Adjustment");

    private static void RequirePending(Adjustment adjustment)
    {
        if (adjustment.Status != AdjustmentStatus.PENDING_APPROVAL)
        {
            throw new ApiException(409, "ADJUSTMENT_NOT_PENDING", "The adjustment has already been decided", null,
                new Dictionary<string, object> { ["status"] = adjustment.Status.ToString() });
        }
    }

    private async Task DecideAsync(Guid id, AdjustmentStatus status, ZawedPrincipal actor, string? reason, Guid? transactionId, CancellationToken ct)
    {
        var updated = await Tx.ExecuteAsync("""
            update wallet_adjustments set status = @status, decided_by = @actor, decided_at = @now,
                   decision_reason = @reason, ledger_transaction_id = @txn, updated_at = @now
            where id = @id and status = 'PENDING_APPROVAL'
            """, new { status = status.ToString(), actor = actor.UserId, now = clock.GetUtcNow().UtcDateTime, reason, txn = transactionId, id }, ct);
        if (updated != 1)
        {
            throw new ApiException(409, "ADJUSTMENT_NOT_PENDING", "The adjustment has already been decided");
        }
    }
}
