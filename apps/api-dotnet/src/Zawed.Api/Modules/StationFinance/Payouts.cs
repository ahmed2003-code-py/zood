using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Modules.StationFinance.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.StationFinance;

public enum PayoutStatus
{
    /// <summary>Above the threshold, waiting for a second person. Its amount is reserved.</summary>
    PENDING,

    /// <summary>Posted to the ledger: the station's payable is debited.</summary>
    POSTED,
    CANCELLED,
}

/// <summary>A payout as stored (Java <c>Payouts.Payout</c>).</summary>
public sealed record Payout(Guid Id, Guid StationId, Guid OperatorId, Guid BankAccountId, long AmountPiastres, string BankReference,
    DateOnly ValueDate, string? Note, PayoutStatus Status, bool ApprovalRequired, long ApprovalThresholdPiastres, Guid RequestedBy,
    DateTimeOffset RequestedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionReason, DateTimeOffset? PostedAt,
    Guid? LedgerTransactionId);

/// <summary>List filters. A null field does not filter.</summary>
public sealed record PayoutFilter(Guid? OperatorId, Guid? StationId, PayoutStatus? Status);

/// <summary>
/// Payouts to stations (roadmap delta A1; plan §3.5: station payable → <c>platform:bank</c>; Java <c>Payouts</c>).
/// Operations records the bank transfer it made: amount, the bank's reference, the value date.
/// <list type="bullet">
/// <item><b>Never more than is there.</b> Under the station's payable lock, the amount must fit in <c>available −
/// payouts waiting for approval</c> (<see cref="PayableBook"/>). Two payouts recorded at the same moment are decided one
/// after the other.</item>
/// <item><b>Dual control.</b> Up to <c>zawed.finance.payout-approval-threshold-piastres</c> (5,000,000 by default) a
/// payout posts at once. Above it, it waits as <c>PENDING</c>, reserving its amount, until a <b>different</b> person
/// approves it; approval checks everything again and only then posts.</item>
/// <item><b>Only to a verified account, outside its hold.</b> The station needs an <c>ACTIVE</c> bank account whose
/// payout hold has passed (<see cref="BankAccounts"/>); a payout that waited while the account changed cannot be
/// approved any more.</item>
/// <item><b>One transfer pays once.</b> The reference, normalised like a top-up's, belongs to at most one pending or
/// posted payout platform-wide; a cancelled payout frees it. The posting's source is the payout id, so a payout can never
/// post twice either.</item>
/// </list>
/// </summary>
public sealed class Payouts(IDbSession session, StationAccess access, BankAccounts bankAccounts, PayableBook book, ILedger ledger,
    IAuditLog audit, TimeProvider clock, StationFinanceSettings settings, IDomainEvents events)
{
    /// <summary>A sanity cap on one payout: 1,000,000,000 EGP. Above it is a typo, not a transfer.</summary>
    public const long MaxAmountPiastres = 100_000_000_000L;

    /// <summary>A transfer ordered today may carry a value date a few days ahead; further is a typing error.</summary>
    public const int MaxValueDateDaysAhead = 7;

    /// <summary>Value dates are business dates in Egypt (PRD §2.3).</summary>
    public static readonly TimeZoneInfo BusinessZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    private DbTx Tx => session.Current;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid Station_Id { get; set; }
        public Guid Operator_Id { get; set; }
        public Guid Bank_Account_Id { get; set; }
        public long Amount_Piastres { get; set; }
        public string Bank_Reference { get; set; } = "";
        public DateOnly Value_Date { get; set; }
        public string? Note { get; set; }
        public string Status { get; set; } = "";
        public bool Approval_Required { get; set; }
        public long Approval_Threshold_Piastres { get; set; }
        public Guid Requested_By { get; set; }
        public DateTime Requested_At { get; set; }
        public Guid? Decided_By { get; set; }
        public DateTime? Decided_At { get; set; }
        public string? Decision_Reason { get; set; }
        public DateTime? Posted_At { get; set; }
        public Guid? Ledger_Transaction_Id { get; set; }

        public Payout ToPayout() => new(Id, Station_Id, Operator_Id, Bank_Account_Id, Amount_Piastres, Bank_Reference, Value_Date, Note,
            Enum.Parse<PayoutStatus>(Status), Approval_Required, Approval_Threshold_Piastres, Requested_By, Finance.Utc(Requested_At), Decided_By,
            Finance.Utc(Decided_At), Decision_Reason, Finance.Utc(Posted_At), Ledger_Transaction_Id);
    }

    // ----------------------------------------------------------------------------------------------------- queries

    public async Task<Page<Payout>> ListAsync(PayoutFilter filter, string? cursor, int? limit, CancellationToken ct)
    {
        var size = Page<Payout>.Limit(limit);
        var rows = await Tx.QueryAsync<Row>("""
            select * from payouts
            where (cast(@operator as uuid) is null or operator_id = @operator)
              and (cast(@station as uuid) is null or station_id = @station)
              and (cast(@status as text) is null or status = @status)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new
        {
            @operator = filter.OperatorId,
            station = filter.StationId,
            status = filter.Status?.ToString(),
            cursor = Cursors.Parse(cursor),
            limit = size + 1,
        }, ct);
        return Page<Payout>.Of(rows.Select(r => r.ToPayout()).ToList(), size, p => p.Id);
    }

    public async Task<Payout> GetAsync(Guid id, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("select * from payouts where id = @id", new { id }, ct))?.ToPayout()
        ?? throw ApiException.NotFound("Payout");

    // ------------------------------------------------------------------------------------------------------ record

    /// <summary>
    /// Records a payout. At or below the threshold it posts now; above it, it is stored as <c>PENDING</c> for a second
    /// person.
    /// </summary>
    public async Task<Payout> RecordAsync(ZawedPrincipal actor, Guid stationId, long amountPiastres, string? bankReference, DateOnly valueDate,
        string? note, CancellationToken ct)
    {
        var reference = await BankReferences.CleanAsync(Tx, bankReference, ct);
        CheckValueDate(valueDate);
        var station = await access.RequireAsync(actor, stationId, ct);

        // Everything below is decided under the station's payable lock.
        await ledger.LockPayableAsync(station.Id, ct);
        var account = await PayableAccountAsync(station.Id, ct);
        RequireAvailable(await book.PositionAsync(station.Id, ct), amountPiastres, 0);
        // Asked before the insert so the common duplicate is a clean refusal; the unique index on the normalised
        // reference is the final word under a race.
        var key = await BankReferences.KeyAsync(Tx, reference, ct);
        if (await ReferenceInUseAsync(key, ct))
        {
            throw ReferenceTaken();
        }

        var now = Finance.Now(clock);
        var id = Guid.CreateVersion7(now);
        var approvalRequired = amountPiastres > settings.PayoutApprovalThresholdPiastres;
        Guid? transactionId = null;
        long? balanceAfter = null;
        if (!approvalRequired)
        {
            var posted = await PostAsync(actor, id, station.Id, amountPiastres, reference, ct);
            transactionId = posted.TransactionId;
            balanceAfter = posted.Payables[0].BalanceAfter;
        }
        try
        {
            await Tx.ExecuteAsync("""
                insert into payouts (id, station_id, operator_id, bank_account_id, amount_piastres, bank_reference,
                                     bank_reference_key, value_date, note, status, approval_threshold_piastres,
                                     approval_required, requested_by, requested_at, posted_at, ledger_transaction_id,
                                     created_at, updated_at)
                values (@id, @station, @operator, @account, @amount, @reference, @key, @valueDate, @note, @status,
                        @threshold, @approvalRequired, @actor, @now, @postedAt, @txn, @now, @now)
                """, new
            {
                id,
                station = station.Id,
                @operator = station.OperatorId,
                account = account.Id,
                amount = amountPiastres,
                reference,
                key,
                valueDate = Finance.Date(valueDate),
                note = Sql.Clean(note),
                status = (approvalRequired ? PayoutStatus.PENDING : PayoutStatus.POSTED).ToString(),
                threshold = settings.PayoutApprovalThresholdPiastres,
                approvalRequired,
                actor = actor.UserId,
                now = now.UtcDateTime,
                postedAt = approvalRequired ? (DateTime?)null : now.UtcDateTime,
                txn = transactionId,
            }, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw ReferenceTaken(); // rolls a posting back with it
        }
        var facts = Facts(station.Id, amountPiastres, reference, account);
        facts["valueDate"] = valueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        facts["approvalRequired"] = approvalRequired;
        if (transactionId is { } txn)
        {
            facts["transactionId"] = txn.ToString();
            facts["payableAfterPiastres"] = balanceAfter;
        }
        await audit.RecordAsync(Tx, Finance.ActorOf(actor), approvalRequired ? "PAYOUT_RECORDED" : "PAYOUT_POSTED", "PAYOUT", id,
            Scope.Operator(station.OperatorId), facts, ct);
        if (!approvalRequired)
        {
            await events.PublishAsync(new PayoutPosted(id, station.OperatorId, station.Id, amountPiastres), ct);
        }
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------------------------------------------------ decide

    /// <summary>
    /// The second person's word on a payout above the threshold. Nothing the first check found is taken on trust: the
    /// account, its hold and what is available are read again under the lock.
    /// </summary>
    public async Task<Payout> ApproveAsync(ZawedPrincipal actor, Guid id, CancellationToken ct)
    {
        var payout = await LockAsync(id, ct);
        RequirePending(payout);
        if (payout.RequestedBy == actor.UserId)
        {
            throw Finance.SelfApproval();
        }
        // Lock order: the payout row, then the payable. Recording a payout takes the payable and then inserts a new row,
        // which nobody else can hold, so the two cannot wait on each other.
        await ledger.LockPayableAsync(payout.StationId, ct);
        var account = await PayableAccountAsync(payout.StationId, ct);
        if (account.Id != payout.BankAccountId)
        {
            throw new ApiException(409, "PAYOUT_BANK_ACCOUNT_CHANGED",
                "The station's bank account changed after this payout was recorded; cancel it and record it again");
        }
        // This payout is itself among the pending ones; it does not compete with itself.
        RequireAvailable(await book.PositionAsync(payout.StationId, ct), payout.AmountPiastres, payout.AmountPiastres);

        var posted = await PostAsync(actor, id, payout.StationId, payout.AmountPiastres, payout.BankReference, ct);
        var now = Finance.Now(clock);
        var updated = await Tx.ExecuteAsync("""
            update payouts set status = 'POSTED', decided_by = @actor, decided_at = @now, posted_at = @now,
                   ledger_transaction_id = @txn, updated_at = @now
            where id = @id and status = 'PENDING'
            """, new { actor = actor.UserId, now = now.UtcDateTime, txn = posted.TransactionId, id }, ct);
        if (updated != 1)
        {
            throw NotPending(payout.Status);
        }
        var facts = Facts(payout.StationId, payout.AmountPiastres, payout.BankReference, account);
        facts["requestedBy"] = payout.RequestedBy.ToString();
        facts["transactionId"] = posted.TransactionId.ToString();
        facts["payableAfterPiastres"] = posted.Payables[0].BalanceAfter;
        await audit.RecordAsync(Tx, Finance.ActorOf(actor), "PAYOUT_APPROVED", "PAYOUT", id, Scope.Operator(payout.OperatorId), facts, ct);
        await events.PublishAsync(new PayoutPosted(id, payout.OperatorId, payout.StationId, payout.AmountPiastres), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Withdraws a payout that still waits. Nothing was posted, so nothing is undone; its reference is free again.</summary>
    public async Task<Payout> CancelAsync(ZawedPrincipal actor, Guid id, string reason, CancellationToken ct)
    {
        var payout = await LockAsync(id, ct);
        RequirePending(payout);
        var now = Finance.Now(clock);
        var updated = await Tx.ExecuteAsync("""
            update payouts set status = 'CANCELLED', decided_by = @actor, decided_at = @now, decision_reason = @reason,
                   updated_at = @now
            where id = @id and status = 'PENDING'
            """, new { actor = actor.UserId, now = now.UtcDateTime, reason, id }, ct);
        if (updated != 1)
        {
            throw NotPending(payout.Status);
        }
        await audit.RecordAsync(Tx, Finance.ActorOf(actor), "PAYOUT_CANCELLED", "PAYOUT", id, Scope.Operator(payout.OperatorId),
            new Dictionary<string, object?>
            {
                ["stationId"] = payout.StationId.ToString(),
                ["amountPiastres"] = payout.AmountPiastres,
                ["bankReference"] = payout.BankReference,
                ["reason"] = reason,
            }, ct);
        return await GetAsync(id, ct);
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    /// <summary>Station payable (debit) to the platform's bank (credit); the source is the payout, which posts once.</summary>
    private Task<Posted> PostAsync(ZawedPrincipal actor, Guid payoutId, Guid stationId, long amountPiastres, string reference,
        CancellationToken ct) =>
        ledger.PostAsync(new Posting(SourceType.PAYOUT, payoutId, reference, "Payout by bank transfer", Poster.Of(actor),
            [Entry.Debit(Account.StationPayable(stationId), amountPiastres), Entry.Credit(Account.PlatformBank(), amountPiastres)]), ct);

    /// <summary>The verified account a payout may go to now: it exists and its hold has passed.</summary>
    private async Task<BankAccount> PayableAccountAsync(Guid stationId, CancellationToken ct)
    {
        var account = await bankAccounts.ActiveAsync(stationId, ct)
            ?? throw new ApiException(409, "BANK_ACCOUNT_REQUIRED", "The station has no verified bank account");
        if (clock.GetUtcNow() < account.PayoutsHeldUntil!.Value)
        {
            throw new ApiException(409, "PAYOUT_ON_HOLD", "Payouts to this station are on hold after its bank account changed", null,
                new Dictionary<string, object> { ["heldUntil"] = Finance.JavaInstant(account.PayoutsHeldUntil.Value) });
        }
        return account;
    }

    /// <param name="ownPending">what this payout already contributes to the pending total (its own amount when it is being
    /// approved, nothing when it is being recorded)</param>
    private static void RequireAvailable(PayablePosition position, long amountPiastres, long ownPending)
    {
        var payable = checked(position.PayablePiastres + ownPending);
        if (amountPiastres > payable)
        {
            throw new ApiException(409, "PAYOUT_EXCEEDS_AVAILABLE", "The payout is more than the station's available payable", "amountPiastres",
                new Dictionary<string, object>
                {
                    ["availablePiastres"] = position.AvailablePiastres,
                    ["pendingPayoutsPiastres"] = position.PendingPayoutsPiastres - ownPending,
                    ["requestedPiastres"] = amountPiastres,
                });
        }
    }

    private Task<bool> ReferenceInUseAsync(string key, CancellationToken ct) => Tx.QuerySingleAsync<bool>("""
        select exists (select 1 from payouts where bank_reference_key = @key and status in ('PENDING', 'POSTED'))
        """, new { key }, ct);

    private void CheckValueDate(DateOnly valueDate)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), BusinessZone).DateTime);
        if (valueDate > today.AddDays(MaxValueDateDaysAhead))
        {
            throw new ApiException(400, "VALUE_DATE_TOO_FAR", "The value date is more than " + MaxValueDateDaysAhead + " days ahead", "valueDate",
                new Dictionary<string, object> { ["maxDaysAhead"] = MaxValueDateDaysAhead });
        }
    }

    /// <summary>Reads the payout and holds its row until commit, so two decisions never both see PENDING.</summary>
    private async Task<Payout> LockAsync(Guid id, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("select * from payouts where id = @id for update", new { id }, ct))?.ToPayout()
        ?? throw ApiException.NotFound("Payout");

    private static void RequirePending(Payout payout)
    {
        if (payout.Status != PayoutStatus.PENDING)
        {
            throw NotPending(payout.Status);
        }
    }

    private static Dictionary<string, object?> Facts(Guid stationId, long amountPiastres, string reference, BankAccount account) => new()
    {
        ["stationId"] = stationId.ToString(),
        ["amountPiastres"] = amountPiastres,
        ["bankReference"] = reference,
        ["bankAccountId"] = account.Id.ToString(),
        ["accountLast4"] = account.AccountLast4,
    };

    private static ApiException NotPending(PayoutStatus status) => new(409, "PAYOUT_NOT_PENDING", "The payout has already been decided", null,
        new Dictionary<string, object> { ["status"] = status.ToString() });

    private static ApiException ReferenceTaken() => new(409, "BANK_REFERENCE_IN_USE",
        "A pending or posted payout already carries this bank reference", "bankReference");
}
