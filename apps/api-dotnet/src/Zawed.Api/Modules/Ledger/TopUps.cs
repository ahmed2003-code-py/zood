using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Ledger;

/// <summary>A top-up request as stored (Java <c>TopUps.TopUp</c>).</summary>
public sealed record TopUp(Guid Id, Guid CompanyId, TopUpOrigin Origin, long AmountPiastres, string ReceivingAccount,
    string BankReference, DateOnly TransferDate, string? Note, TopUpStatus Status, Guid? RequestedBy, DateTimeOffset RequestedAt,
    long? VerifiedAmountPiastres, Guid? VerifiedBy, DateTimeOffset? VerifiedAt, string? EvidenceReference, Guid? DecidedBy,
    DateTimeOffset? DecidedAt, string? DecisionReason, Guid? LedgerTransactionId);

/// <summary>What Operations read on the bank statement.</summary>
public sealed record Verification(string? ReceivingAccount, string? BankReference, long ReceivedAmountPiastres, DateOnly? TransferDate,
    string? EvidenceReference, string? Note);

/// <summary>A recorded transfer, and whether it was this request sent again.</summary>
public sealed record Recorded(TopUp TopUp, bool Replayed);

/// <summary>
/// Top-up by bank transfer (PRD §4.6, §6.3; ADR 0021, maker-checker; Java <c>TopUps</c>). Money enters a wallet in three
/// steps, and two different people take the last two:
/// <list type="number">
/// <item>The company says it has transferred money (amount, reference, date): <c>PENDING</c>. A claim, never proof, and
/// nothing moves. Operations may also record a transfer the company never announced: that row starts at step 2.</item>
/// <item>An Operations user finds the transfer on ZAWED's bank statement and <b>verifies</b> it: which receiving
/// account, the reference as the statement shows it, and the amount that actually arrived: <c>PENDING_APPROVAL</c>.</item>
/// <item>A <b>different</b> user holding <c>TOPUP_APPROVE</c> approves: <c>CONFIRMED</c>, posting <c>platform:bank</c> →
/// the company wallet for exactly the verified amount, never the claimed one.</item>
/// </list>
/// One bank transfer credits once: a reference, normalised, belongs to at most one live top-up per receiving account
/// (a unique index), and the posting's source is the top-up id, so even a confirmed top-up can never post twice. The
/// two-person rule and "the verifier and the approver are the acting users" are also database rules (V12, V17).
/// <para>Refusals are thrown as <see cref="ApiException"/>, as the Java service throws them; the request's transaction
/// rolls back.</para>
/// </summary>
public sealed class TopUps(IDbSession session, ILedger ledger, IAuditLog audit, TimeProvider clock, FinanceSettings finance,
    IDomainEvents events)
{
    /// <summary>Transfer dates are business dates in Egypt (PRD §2.3: local time for display and rules).</summary>
    public static readonly TimeZoneInfo BusinessZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    private DbTx Tx => session.Current;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid Company_Id { get; set; }
        public string Origin { get; set; } = "";
        public long Amount_Piastres { get; set; }
        public string Receiving_Account { get; set; } = "";
        public string Bank_Reference { get; set; } = "";
        public DateOnly Transfer_Date { get; set; }
        public string? Note { get; set; }
        public string Status { get; set; } = "";
        public Guid? Requested_By { get; set; }
        public DateTime Requested_At { get; set; }
        public long? Verified_Amount_Piastres { get; set; }
        public Guid? Verified_By { get; set; }
        public DateTime? Verified_At { get; set; }
        public string? Evidence_Reference { get; set; }
        public Guid? Decided_By { get; set; }
        public DateTime? Decided_At { get; set; }
        public string? Decision_Reason { get; set; }
        public Guid? Ledger_Transaction_Id { get; set; }

        public TopUp ToTopUp() => new(Id, Company_Id, Enum.Parse<TopUpOrigin>(Origin), Amount_Piastres, Receiving_Account, Bank_Reference,
            Transfer_Date, Note, Enum.Parse<TopUpStatus>(Status), Requested_By, LedgerTime.Utc(Requested_At),
            Verified_Amount_Piastres, Verified_By, LedgerTime.Utc(Verified_At), Evidence_Reference, Decided_By, LedgerTime.Utc(Decided_At),
            Decision_Reason, Ledger_Transaction_Id);
    }

    // ----------------------------------------------------------------------------------------------------- queries

    public async Task<Page<TopUp>> ListAsync(Guid? companyId, TopUpStatus? status, string? cursor, int? limit, CancellationToken ct)
    {
        var size = Page<TopUp>.Limit(limit);
        var rows = await Tx.QueryAsync<Row>("""
            select * from topup_requests
            where (cast(@company as uuid) is null or company_id = @company)
              and (cast(@status as text) is null or status = @status)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new { company = companyId, status = status?.ToString(), cursor = Cursors.Parse(cursor), limit = size + 1 }, ct);
        return Page<TopUp>.Of(rows.Select(r => r.ToTopUp()).ToList(), size, t => t.Id);
    }

    public async Task<TopUp> GetAsync(Guid id, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("select * from topup_requests where id = @id", new { id }, ct))?.ToTopUp()
        ?? throw ApiException.NotFound("Top-up");

    /// <summary>The receiving accounts Operations may name, for the Ops screen, sorted.</summary>
    public IReadOnlyList<string> ReceivingAccounts() => finance.ReceivingAccounts.Order(StringComparer.Ordinal).ToList();

    // ------------------------------------------------------------------------------------------------- the company

    /// <summary>A company user reports a transfer. A claim: nothing moves until two Operations users agree.</summary>
    public async Task<TopUp> RequestAsync(ZawedPrincipal actor, Guid companyId, long amountPiastres, string? bankReference,
        DateOnly transferDate, string? note, CancellationToken ct)
    {
        var reference = await BankReferences.CleanAsync(Tx, bankReference, ct);
        var key = await BankReferences.KeyAsync(Tx, reference, ct);
        CheckTransferDate(transferDate);
        await RequireWalletAsync(companyId, ct);
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        try
        {
            await Tx.ExecuteAsync("""
                insert into topup_requests (id, company_id, origin, amount_piastres, receiving_account, bank_reference,
                                            bank_reference_key, transfer_date, note, status, requested_by, requested_at,
                                            created_at, updated_at)
                values (@id, @company, 'COMPANY_REQUEST', @amount, @account, @reference, @key, @date, @note, 'PENDING',
                        @actor, @now, @now, @now)
                """, new
            {
                id,
                company = companyId,
                amount = amountPiastres,
                account = FinanceSettings.DefaultReceivingAccount,
                reference,
                key,
                date = LedgerTime.Date(transferDate),
                note = Sql.Clean(note),
                actor = actor.UserId,
                now = now.UtcDateTime,
            }, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw ReferenceInUse();
        }
        await audit.RecordAsync(Tx, ActorOf(actor), "TOPUP_REQUESTED", "TOPUP", id, Scope.Company(companyId), new Dictionary<string, object?>
        {
            ["amountPiastres"] = amountPiastres,
            ["bankReference"] = reference,
            ["transferDate"] = transferDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        }, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>The company withdraws a request nobody has verified yet.</summary>
    public async Task<TopUp> CancelAsync(ZawedPrincipal actor, Guid companyId, Guid id, CancellationToken ct)
    {
        // Read first: a company's row lock needs its UPDATE policy, which only a PENDING request passes, so a verified
        // or decided request would otherwise answer 404 instead of saying why.
        RequireStatus(await GetAsync(id, ct), TopUpStatus.PENDING);
        var topUp = await LockAsync(id, ct);
        if (topUp.CompanyId != companyId)
        {
            throw ApiException.NotFound("Top-up"); // never reached under row-level security; kept as the API rule
        }
        RequireStatus(topUp, TopUpStatus.PENDING);
        await DecideAsync(id, TopUpStatus.PENDING, TopUpStatus.CANCELLED, actor, null, null, ct);
        await audit.RecordAsync(Tx, ActorOf(actor), "TOPUP_CANCELLED", "TOPUP", id, Scope.Company(companyId), new Dictionary<string, object?>
        {
            ["amountPiastres"] = topUp.AmountPiastres,
            ["bankReference"] = topUp.BankReference,
        }, ct);
        return await GetAsync(id, ct);
    }

    // ---------------------------------------------------------------------------------------------- step 2: verify

    /// <summary>
    /// Operations has found the company's transfer on ZAWED's statement and records what arrived. The reference may be
    /// corrected to the statement's; the amount is what the bank shows.
    /// </summary>
    public async Task<TopUp> VerifyAsync(ZawedPrincipal actor, Guid id, Verification v, CancellationToken ct)
    {
        var topUp = await LockAsync(id, ct);
        RequireStatus(topUp, TopUpStatus.PENDING);
        var account = ReceivingAccount(v.ReceivingAccount);
        var reference = Sql.Clean(v.BankReference) is null ? topUp.BankReference : await BankReferences.CleanAsync(Tx, v.BankReference, ct);
        var key = await BankReferences.KeyAsync(Tx, reference, ct);
        var transferDate = v.TransferDate ?? topUp.TransferDate;
        CheckTransferDate(transferDate);
        var now = clock.GetUtcNow();
        try
        {
            var updated = await Tx.ExecuteAsync("""
                update topup_requests
                set status = 'PENDING_APPROVAL', receiving_account = @account, bank_reference = @reference,
                    bank_reference_key = @key, transfer_date = @date, verified_amount_piastres = @amount,
                    verified_by = @actor, verified_at = @now, evidence_reference = @evidence,
                    note = coalesce(@note, note), updated_at = @now
                where id = @id and status = 'PENDING'
                """, new
            {
                account,
                reference,
                key,
                date = LedgerTime.Date(transferDate),
                amount = v.ReceivedAmountPiastres,
                actor = actor.UserId,
                now = now.UtcDateTime,
                evidence = Sql.Clean(v.EvidenceReference),
                note = Sql.Clean(v.Note),
                id,
            }, ct);
            if (updated != 1)
            {
                throw NotInStatus(TopUpStatus.PENDING, topUp.Status);
            }
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw ReferenceInUse();
        }
        var facts = new Dictionary<string, object?>
        {
            ["claimedAmountPiastres"] = topUp.AmountPiastres,
            ["verifiedAmountPiastres"] = v.ReceivedAmountPiastres,
            ["receivingAccount"] = account,
            ["bankReference"] = reference,
        };
        if (reference != topUp.BankReference)
        {
            facts["bankReferenceRequested"] = topUp.BankReference;
        }
        if (Sql.Clean(v.EvidenceReference) is { } evidence)
        {
            facts["evidenceReference"] = evidence;
        }
        await audit.RecordAsync(Tx, ActorOf(actor), "TOPUP_VERIFIED", "TOPUP", id, Scope.Company(topUp.CompanyId), facts, ct);
        await events.PublishAsync(new TopUpAwaitingApproval(id, topUp.CompanyId, v.ReceivedAmountPiastres), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Operations records a transfer that arrived without a company request (PRD §6.3 "Credit Wallet"). It is verified by
    /// the recorder and still waits for a second person. Sending the same record again answers the existing record
    /// (<c>Replayed</c>): the same account and reference, and the same company, amount, transfer date, evidence and note,
    /// on a record Operations made. The same reference with anything else different is <c>409 BANK_REFERENCE_IN_USE</c>,
    /// never a second top-up and never another request's record passed off as this one.
    /// </summary>
    public async Task<Recorded> RecordTransferAsync(ZawedPrincipal actor, Guid companyId, Verification v, CancellationToken ct)
    {
        var account = ReceivingAccount(v.ReceivingAccount);
        var reference = await BankReferences.CleanAsync(Tx, v.BankReference, ct);
        var key = await BankReferences.KeyAsync(Tx, reference, ct);
        if (v.TransferDate is not { } transferDate)
        {
            throw new ApiException(400, "VALIDATION_FAILED", "The transfer date is required", "transferDate");
        }
        CheckTransferDate(transferDate);
        await RequireWalletAsync(companyId, ct);
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        // ON CONFLICT, not a caught duplicate-key error: a failed INSERT aborts the whole transaction in PostgreSQL, and
        // the replay below must still read the existing record.
        var inserted = await Tx.ExecuteAsync("""
            insert into topup_requests (id, company_id, origin, amount_piastres, receiving_account, bank_reference,
                                        bank_reference_key, transfer_date, note, status, requested_by, requested_at,
                                        verified_amount_piastres, verified_by, verified_at, evidence_reference,
                                        created_at, updated_at)
            values (@id, @company, 'OPS_RECORDED', @amount, @account, @reference, @key, @date, @note,
                    'PENDING_APPROVAL', null, @now, @amount, @actor, @now, @evidence, @now, @now)
            on conflict do nothing
            """, new
        {
            id,
            company = companyId,
            amount = v.ReceivedAmountPiastres,
            account,
            reference,
            key,
            date = LedgerTime.Date(transferDate),
            note = Sql.Clean(v.Note),
            actor = actor.UserId,
            evidence = Sql.Clean(v.EvidenceReference),
            now = now.UtcDateTime,
        }, ct);
        if (inserted == 0)
        {
            var live = await LiveByReferenceAsync(account, key, ct) ?? throw ReferenceInUse();
            // A replay is this request sent again, field for field (review finding, B7 W1): a retry that differs in its
            // date, evidence or note is another claim about the transfer, and a company's verified request is not an Ops
            // record. The typed reference may differ in spelling only (its key is equal), which the statement cannot
            // tell apart either.
            var same = live.Origin == TopUpOrigin.OPS_RECORDED && live.CompanyId == companyId
                && live.VerifiedAmountPiastres is { } verified && verified == v.ReceivedAmountPiastres
                && live.TransferDate == transferDate
                && live.EvidenceReference == Sql.Clean(v.EvidenceReference)
                && live.Note == Sql.Clean(v.Note);
            if (!same)
            {
                throw ReferenceInUse();
            }
            return new Recorded(live, true);
        }
        await audit.RecordAsync(Tx, ActorOf(actor), "TOPUP_RECORDED", "TOPUP", id, Scope.Company(companyId), new Dictionary<string, object?>
        {
            ["verifiedAmountPiastres"] = v.ReceivedAmountPiastres,
            ["receivingAccount"] = account,
            ["bankReference"] = reference,
            ["transferDate"] = transferDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        }, ct);
        await events.PublishAsync(new TopUpAwaitingApproval(id, companyId, v.ReceivedAmountPiastres), ct);
        return new Recorded(await GetAsync(id, ct), false);
    }

    // --------------------------------------------------------------------------------------------- step 3: approve

    /// <summary>
    /// A different person approves a verified top-up: the wallet is credited the verified amount, once. Approving a
    /// top-up that is already confirmed answers it as it is and posts nothing.
    /// </summary>
    public async Task<TopUp> ApproveAsync(ZawedPrincipal actor, Guid id, CancellationToken ct)
    {
        var topUp = await LockAsync(id, ct);
        if (topUp.Status == TopUpStatus.CONFIRMED)
        {
            return topUp; // a repeated approval (a retry, a double click): already credited, exactly once
        }
        RequireStatus(topUp, TopUpStatus.PENDING_APPROVAL);
        if (actor.UserId == topUp.VerifiedBy)
        {
            throw new ApiException(403, "SELF_APPROVAL_NOT_ALLOWED", "The person who verified a transfer cannot approve it");
        }
        // The wallet after the top-up row: every path that credits a wallet takes the request row first and the wallet
        // second, and a fueling takes only the wallet, so no cycle can form.
        await ledger.LockWalletAsync(topUp.CompanyId, ct);
        var amount = topUp.VerifiedAmountPiastres!.Value;
        var posted = await ledger.PostAsync(new Posting(SourceType.TOPUP, id, topUp.BankReference, "Top-up by bank transfer",
            Poster.Of(actor), [Entry.Debit(Account.PlatformBank(), amount), Entry.Credit(Account.CompanyWallet(topUp.CompanyId), amount)]), ct);
        await DecideAsync(id, TopUpStatus.PENDING_APPROVAL, TopUpStatus.CONFIRMED, actor, null, posted.TransactionId, ct);
        await audit.RecordAsync(Tx, ActorOf(actor), "TOPUP_CONFIRMED", "TOPUP", id, Scope.Company(topUp.CompanyId), new Dictionary<string, object?>
        {
            ["creditedPiastres"] = amount,
            ["claimedAmountPiastres"] = topUp.AmountPiastres,
            ["verifiedBy"] = topUp.VerifiedBy!.Value.ToString(),
            ["bankReference"] = topUp.BankReference,
            ["receivingAccount"] = topUp.ReceivingAccount,
            ["transactionId"] = posted.TransactionId.ToString(),
            ["balanceAfterPiastres"] = posted.Wallets[0].BalanceAfter,
        }, ct);
        await events.PublishAsync(new TopUpCredited(id, topUp.CompanyId, amount, posted.TransactionId), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Refuses a claim (no matching transfer) or a verification (the approver disagrees).</summary>
    public async Task<TopUp> RejectAsync(ZawedPrincipal actor, Guid id, string reason, CancellationToken ct)
    {
        var topUp = await LockAsync(id, ct);
        if (topUp.Status is not TopUpStatus.PENDING and not TopUpStatus.PENDING_APPROVAL)
        {
            throw NotInStatus(TopUpStatus.PENDING, topUp.Status);
        }
        await DecideAsync(id, topUp.Status, TopUpStatus.REJECTED, actor, reason, null, ct);
        await audit.RecordAsync(Tx, ActorOf(actor), "TOPUP_REJECTED", "TOPUP", id, Scope.Company(topUp.CompanyId), new Dictionary<string, object?>
        {
            ["amountPiastres"] = topUp.AmountPiastres,
            ["bankReference"] = topUp.BankReference,
            ["reason"] = reason,
            ["stage"] = topUp.Status.ToString(),
        }, ct);
        await events.PublishAsync(new TopUpRejected(id, topUp.CompanyId, topUp.Origin == TopUpOrigin.COMPANY_REQUEST, topUp.AmountPiastres), ct);
        return await GetAsync(id, ct);
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    private string ReceivingAccount(string? requested)
    {
        var account = Sql.Clean(requested) is null ? FinanceSettings.DefaultReceivingAccount : JavaText.ToUpperRoot(JavaText.Trim(requested!));
        if (!finance.ReceivingAccounts.Contains(account))
        {
            throw new ApiException(400, "UNKNOWN_RECEIVING_ACCOUNT", "This is not one of ZAWED's receiving bank accounts", "receivingAccount",
                new Dictionary<string, object> { ["accounts"] = ReceivingAccounts() });
        }
        return account;
    }

    private async Task<TopUp?> LiveByReferenceAsync(string account, string key, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("""
            select * from topup_requests
            where receiving_account = @account and bank_reference_key = @key
              and status in ('PENDING', 'PENDING_APPROVAL', 'CONFIRMED')
            """, new { account, key }, ct))?.ToTopUp();

    private void CheckTransferDate(DateOnly transferDate)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), BusinessZone).DateTime);
        if (transferDate > today)
        {
            throw new ApiException(400, "TRANSFER_DATE_IN_FUTURE", "The transfer date is in the future", "transferDate");
        }
    }

    private async Task RequireWalletAsync(Guid companyId, CancellationToken ct)
    {
        if (await Tx.QuerySingleOrDefaultAsync<int?>("select 1 from company_wallets where company_id = @company", new { company = companyId }, ct) is null)
        {
            throw ApiException.NotFound("Wallet");
        }
    }

    /// <summary>Reads the top-up and holds its row until commit, so two decisions never both see it open.</summary>
    private async Task<TopUp> LockAsync(Guid id, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("select * from topup_requests where id = @id for update", new { id }, ct))?.ToTopUp()
        ?? throw ApiException.NotFound("Top-up");

    private static void RequireStatus(TopUp topUp, TopUpStatus expected)
    {
        if (topUp.Status != expected)
        {
            throw NotInStatus(expected, topUp.Status);
        }
    }

    private static ApiException NotInStatus(TopUpStatus expected, TopUpStatus actual) =>
        new(409, expected == TopUpStatus.PENDING_APPROVAL ? "TOPUP_NOT_AWAITING_APPROVAL" : "TOPUP_NOT_PENDING",
            "The top-up is not in the expected state", null, new Dictionary<string, object> { ["status"] = actual.ToString() });

    /// <summary>The conditional flip: the row count says whether it was still in the expected state.</summary>
    private async Task DecideAsync(Guid id, TopUpStatus from, TopUpStatus to, ZawedPrincipal actor, string? reason, Guid? transactionId,
        CancellationToken ct)
    {
        var updated = await Tx.ExecuteAsync("""
            update topup_requests set status = @to, decided_by = @actor, decided_at = @now,
                   decision_reason = @reason, ledger_transaction_id = @txn, updated_at = @now
            where id = @id and status = @from
            """, new
        {
            to = to.ToString(),
            from = from.ToString(),
            actor = actor.UserId,
            now = clock.GetUtcNow().UtcDateTime,
            reason,
            txn = transactionId,
            id,
        }, ct);
        if (updated != 1)
        {
            throw NotInStatus(from, to);
        }
    }

    private static ApiException ReferenceInUse() => new(409, "BANK_REFERENCE_IN_USE",
        "A live top-up on this receiving account already carries this bank reference", "bankReference");

    internal static AuditActor ActorOf(ZawedPrincipal actor) => new(actor.UserId, actor.Role);
}
