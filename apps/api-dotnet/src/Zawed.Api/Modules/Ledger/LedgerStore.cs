using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Modules.Tenancy.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Ledger;

/// <summary>
/// The posting engine (Java <c>LedgerStore</c>). Explicit SQL and explicit locks (ADR 0004): every step that touches
/// money is visible here, and the database checks the result again at commit (V6 triggers and constraints). Runs in
/// the caller's transaction, <see cref="IDbSession.Current"/>.
/// </summary>
public sealed class LedgerStore(IDbSession session, TimeProvider clock, IAuditLog audit, ICompanyDirectory companies, IDomainEvents events)
    : ILedger
{
    public const string AlreadyPosted = "ALREADY_POSTED";
    public const string AlreadyReversed = "ALREADY_REVERSED";
    public const string InsufficientBalance = "INSUFFICIENT_BALANCE";

    /// <summary>Audit actions for a company receivable: a debit raises the debt, a credit settles it.</summary>
    public const string ReceivableRaised = "COMPANY_RECEIVABLE_RAISED";

    public const string ReceivableSettled = "COMPANY_RECEIVABLE_SETTLED";

    private DbTx Tx => session.Current;

    internal sealed class WalletRow
    {
        public Guid Company_Id { get; set; }
        public Guid Account_Id { get; set; }
        public long Balance_Piastres { get; set; }
        public DateTime Updated_At { get; set; }

        public Wallet ToWallet() => new(Company_Id, Account_Id, Balance_Piastres, LedgerTime.Utc(Updated_At));
    }

    private sealed class PayableRow
    {
        public Guid Station_Id { get; set; }
        public Guid Account_Id { get; set; }
        public long Balance_Piastres { get; set; }
        public DateTime Updated_At { get; set; }
    }

    private sealed class MirrorRow
    {
        public string Kind { get; set; } = "";
        public Guid? Company_Id { get; set; }
        public Guid? Station_Id { get; set; }
        public string Direction { get; set; } = "";
        public long Amount_Piastres { get; set; }
    }

    public async Task<Wallet> LockWalletAsync(Guid companyId, CancellationToken ct = default)
    {
        var row = await Tx.QuerySingleOrDefaultAsync<WalletRow>("""
            select company_id, account_id, balance_piastres, updated_at from company_wallets
            where company_id = @company for update
            """, new { company = companyId }, ct);
        return row?.ToWallet() ?? throw ApiException.NotFound("Wallet");
    }

    public Task<Payable> LockPayableAsync(Guid stationId, CancellationToken ct = default) => LockPayableAsync(stationId, clock.GetUtcNow(), ct);

    public Task<Posted> PostAsync(Posting posting, CancellationToken ct = default)
    {
        if (posting.SourceType == SourceType.REVERSAL)
        {
            throw new ArgumentException("A reversal is posted with ReverseAsync(), which mirrors the original");
        }
        return PostAsync(posting, null, ct);
    }

    public async Task<Posted> ReverseAsync(Reversal reversal, CancellationToken ct = default)
    {
        // The mirror is built here from what the original actually posted, not from what a caller believes it
        // posted: same accounts, same amounts, each direction flipped, in the same order.
        var rows = await Tx.QueryAsync<MirrorRow>("""
            select a.kind, a.company_id, a.station_id, e.direction, e.amount_piastres
            from ledger_entries e join ledger_accounts a on a.id = e.account_id
            where e.transaction_id = @txn order by e.seq
            """, new { txn = reversal.OriginalTransactionId }, ct);
        var mirror = rows.Select(r =>
        {
            var kind = Enum.Parse<AccountKind>(r.Kind);
            var owner = kind switch
            {
                AccountKind.PLATFORM_BANK or AccountKind.PLATFORM_ADJUSTMENT => null,
                AccountKind.COMPANY_WALLET or AccountKind.COMPANY_RECEIVABLE => r.Company_Id,
                _ => r.Station_Id,
            };
            var flipped = Enum.Parse<Direction>(r.Direction) == Direction.DEBIT ? Direction.CREDIT : Direction.DEBIT;
            return new Entry(new Account(kind, owner), flipped, r.Amount_Piastres);
        }).ToList();
        if (mirror.Count == 0)
        {
            throw ApiException.NotFound("Ledger transaction");
        }
        return await PostAsync(new Posting(SourceType.REVERSAL, reversal.ReversalId, reversal.Reference, reversal.Description,
            reversal.Poster, mirror), reversal.OriginalTransactionId, ct);
    }

    /// <param name="reverses">the transaction this posting mirrors, for a reversal; null otherwise</param>
    private async Task<Posted> PostAsync(Posting posting, Guid? reverses, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var tx = Tx;

        // 1. Lock every wallet the posting touches, always in ascending company id (Java's UUID order, so a Java and a
        //    .NET instance lock alike), so two postings that touch the same wallets can never wait on each other in a
        //    cycle. Then every station payable, in ascending station id. Wallets always come before payables
        //    (ADR 0020): a fueling holds its company's wallet when it reaches here and a payout holds no wallet at all.
        var wallets = new SortedDictionary<Guid, Wallet?>(JavaUuidOrder.Instance);
        var payables = new SortedDictionary<Guid, Payable?>(JavaUuidOrder.Instance);
        foreach (var entry in posting.Entries)
        {
            if (entry.Account.Kind == AccountKind.COMPANY_WALLET)
            {
                wallets.TryAdd(entry.Account.OwnerId!.Value, null);
            }
            else if (entry.Account.Kind == AccountKind.STATION_PAYABLE)
            {
                payables.TryAdd(entry.Account.OwnerId!.Value, null);
            }
        }
        foreach (var walletOwner in wallets.Keys.ToList())
        {
            wallets[walletOwner] = await LockWalletAsync(walletOwner, ct);
        }
        foreach (var stationId in payables.Keys.ToList())
        {
            payables[stationId] = await LockPayableAsync(stationId, now, ct);
        }

        // 2. A source posts once, and a transaction is reversed once. Checked here, after the locks, so a concurrent
        //    duplicate (which waited on the same wallet or payable lock) sees the first posting and is refused without
        //    aborting the caller's database transaction; the unique indexes below are the final guard.
        if (await SourcePostedAsync(posting.SourceType, posting.SourceId, ct))
        {
            throw AlreadyPostedError();
        }
        if (reverses is { } original && await ReversedAsync(original, ct))
        {
            throw AlreadyReversedError();
        }

        // 3. Accounts, then the transaction row.
        var accountIds = new Dictionary<Account, Guid>();
        foreach (var entry in posting.Entries)
        {
            if (!accountIds.ContainsKey(entry.Account))
            {
                accountIds[entry.Account] = await AccountIdAsync(entry.Account, wallets, payables, now, ct);
            }
        }
        var transactionId = Guid.CreateVersion7(now);
        // The one company whose wallet or receivable the posting touches, so that company can read the transaction
        // under row-level security; null when there is none or more than one.
        var companyIds = posting.Entries.Select(e => e.Account.CompanyId).OfType<Guid>().Distinct().ToList();
        Guid? companyId = companyIds.Count == 1 ? companyIds[0] : null;
        try
        {
            await tx.ExecuteAsync("""
                insert into ledger_transactions (id, source_type, source_id, company_id, reference, description,
                                                 posted_at, posted_by, reverses_transaction_id)
                values (@id, @sourceType, @sourceId, @company, @reference, @description, @now, @actor, @reverses)
                """, new
            {
                id = transactionId,
                sourceType = posting.SourceType.ToString(),
                sourceId = posting.SourceId,
                company = companyId,
                reference = posting.Reference,
                description = posting.Description,
                now = now.UtcDateTime,
                actor = posting.Poster.UserId,
                reverses,
            }, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Two unique rules can refuse the row: one posting per source, one reversal per original.
            throw DbErrors.Postgres(ex)!.ConstraintName == "ledger_transactions_reversed_once" ? AlreadyReversedError() : AlreadyPostedError();
        }

        // 4. The entries, in order. A wallet or payable entry moves the cached balance under the lock taken in step 1
        //    and records the balance it leaves behind.
        var balances = wallets.ToDictionary(w => w.Key, w => w.Value!.BalancePiastres);
        var owed = payables.ToDictionary(p => p.Key, p => p.Value!.BalancePiastres);
        foreach (var entry in posting.Entries)
        {
            long? balanceAfter = null;
            // Wallet and receivable entries carry their company (the V6 trigger checks it against the account). Only a
            // wallet entry has a cached balance to move and a floor to respect.
            var entryCompany = entry.Account.CompanyId;
            if (entry.Account.Kind == AccountKind.COMPANY_WALLET)
            {
                var before = balances[entryCompany!.Value];
                var after = entry.Direction == Direction.CREDIT ? checked(before + entry.Piastres) : checked(before - entry.Piastres);
                if (after < 0)
                {
                    throw new ApiException(409, InsufficientBalance, "The wallet balance is not enough", null,
                        new Dictionary<string, object> { ["balancePiastres"] = before, ["requiredPiastres"] = entry.Piastres });
                }
                await UpdateBalanceAsync(entryCompany.Value, after, now, ct);
                balances[entryCompany.Value] = after;
                balanceAfter = after;
            }
            else if (entry.Account.Kind == AccountKind.STATION_PAYABLE)
            {
                // A payable is a liability: a credit raises what the station is owed, a debit (a payout, a reversal)
                // lowers it. There is no floor: below zero the station owes the platform.
                var stationId = entry.Account.OwnerId!.Value;
                var before = owed[stationId];
                var after = entry.Direction == Direction.CREDIT ? checked(before + entry.Piastres) : checked(before - entry.Piastres);
                await UpdatePayableAsync(stationId, after, now, ct);
                owed[stationId] = after;
                balanceAfter = after;
            }
            await tx.ExecuteAsync("""
                insert into ledger_entries (id, transaction_id, account_id, company_id, direction, amount_piastres,
                                            balance_after_piastres, posted_at)
                values (@id, @txn, @account, @company, @direction, @amount, @after, @now)
                """, new
            {
                id = Guid.CreateVersion7(now),
                txn = transactionId,
                account = accountIds[entry.Account],
                company = entryCompany,
                direction = entry.Direction.ToString(),
                amount = entry.Piastres,
                after = balanceAfter,
                now = now.UtcDateTime,
            }, ct);
        }

        // 5. A company's debt never changes silently: every receivable entry is audited with the posting that made it
        //    (B5: fuel taken offline that the wallet could not cover).
        foreach (var entry in posting.Entries.Where(e => e.Account.Kind == AccountKind.COMPANY_RECEIVABLE))
        {
            var debtor = entry.Account.OwnerId!.Value;
            await audit.RecordAsync(tx, new AuditActor(posting.Poster.UserId, posting.Poster.Role),
                entry.Direction == Direction.DEBIT ? ReceivableRaised : ReceivableSettled, "COMPANY_RECEIVABLE", debtor, Scope.Company(debtor),
                new Dictionary<string, object?>
                {
                    ["amountPiastres"] = entry.Piastres,
                    ["sourceType"] = posting.SourceType.ToString(),
                    ["sourceId"] = posting.SourceId.ToString(),
                    ["transactionId"] = transactionId.ToString(),
                }, ct);
        }

        // 6. Low balance (PRD 4.7): recorded once, when a posting crosses the threshold downwards.
        var changes = new List<WalletChange>();
        foreach (var (walletCompany, wallet) in wallets)
        {
            var threshold = (await companies.FindAsync(walletCompany, ct)
                ?? throw new InvalidOperationException($"Wallet {walletCompany} has no visible company")).LowBalanceThresholdPiastres;
            var change = new WalletChange(walletCompany, wallet!.BalancePiastres, balances[walletCompany], threshold);
            changes.Add(change);
            if (change.CrossedBelowThreshold)
            {
                await audit.RecordAsync(tx, new AuditActor(posting.Poster.UserId, posting.Poster.Role), "WALLET_LOW_BALANCE", "COMPANY_WALLET",
                    change.CompanyId, Scope.Company(change.CompanyId), new Dictionary<string, object?>
                    {
                        ["balancePiastres"] = change.BalanceAfter,
                        ["thresholdPiastres"] = threshold,
                        ["transactionId"] = transactionId.ToString(),
                    }, ct);
                await events.PublishAsync(new WalletLowBalance(change.CompanyId, change.BalanceAfter, threshold, transactionId), ct);
            }
        }
        var payableChanges = payables.Select(p => new PayableChange(p.Key, p.Value!.BalancePiastres, owed[p.Key])).ToList();
        return new Posted(transactionId, changes.AsReadOnly(), payableChanges.AsReadOnly());
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private Task<bool> SourcePostedAsync(SourceType sourceType, Guid sourceId, CancellationToken ct) =>
        Tx.QuerySingleAsync<bool>("""
            select exists (select 1 from ledger_transactions where source_type = @type and source_id = @id)
            """, new { type = sourceType.ToString(), id = sourceId }, ct);

    private Task<bool> ReversedAsync(Guid transactionId, CancellationToken ct) =>
        Tx.QuerySingleAsync<bool>("""
            select exists (select 1 from ledger_transactions where reverses_transaction_id = @id)
            """, new { id = transactionId }, ct);

    /// <summary>
    /// The station's payable row, locked; opened on first use. The row carries the station's operator (for row-level
    /// security) and starts at the sum of the account's entries, which is zero for a new account and keeps a payable
    /// opened before V9 right if its row were ever missing.
    /// </summary>
    private async Task<Payable> LockPayableAsync(Guid stationId, DateTimeOffset now, CancellationToken ct)
    {
        if (await SelectPayableForUpdateAsync(stationId, ct) is { } existing)
        {
            return existing;
        }
        var code = Account.StationPayable(stationId).Code;
        var accountId = await Tx.QuerySingleOrDefaultAsync<Guid?>("select id from ledger_accounts where code = @code", new { code }, ct);
        if (accountId is null)
        {
            await RequireStationAsync(stationId, ct);
            accountId = await OpenAccountAsync(Account.StationPayable(stationId), now, ct);
        }
        // ON CONFLICT for the same reason as OpenAccountAsync: two first postings to one station race here, and the
        // loser must find the winner's row instead of aborting its transaction.
        await Tx.ExecuteAsync("""
            insert into station_payables (station_id, operator_id, account_id, balance_piastres, created_at, updated_at)
            select s.id, s.operator_id, @account,
                   coalesce((select sum(case e.direction when 'CREDIT' then e.amount_piastres else -e.amount_piastres end)
                               from ledger_entries e where e.account_id = @account), 0),
                   @now, @now
            from stations s where s.id = @station
            on conflict do nothing
            """, new { station = stationId, account = accountId, now = now.UtcDateTime }, ct);
        return await SelectPayableForUpdateAsync(stationId, ct) ?? throw ApiException.NotFound("Station");
    }

    private async Task<Payable?> SelectPayableForUpdateAsync(Guid stationId, CancellationToken ct)
    {
        var row = await Tx.QuerySingleOrDefaultAsync<PayableRow>("""
            select station_id, account_id, balance_piastres, updated_at from station_payables
            where station_id = @station for update
            """, new { station = stationId }, ct);
        return row is null ? null : new Payable(row.Station_Id, row.Account_Id, row.Balance_Piastres, LedgerTime.Utc(row.Updated_At));
    }

    private async Task RequireStationAsync(Guid stationId, CancellationToken ct)
    {
        if (await Tx.QuerySingleOrDefaultAsync<int?>("select 1 from stations where id = @id", new { id = stationId }, ct) is null)
        {
            throw ApiException.NotFound("Station");
        }
    }

    /// <summary>
    /// The account row for an account. Wallet and payable accounts come from their locked rows (so one that does not
    /// exist was refused before anything was written); the bank account exists since V6; a company receivable is
    /// opened on first use.
    /// </summary>
    private async Task<Guid> AccountIdAsync(Account account, IDictionary<Guid, Wallet?> wallets, IDictionary<Guid, Payable?> payables,
        DateTimeOffset now, CancellationToken ct)
    {
        if (account.Kind == AccountKind.COMPANY_WALLET)
        {
            return wallets[account.OwnerId!.Value]!.AccountId;
        }
        if (account.Kind == AccountKind.STATION_PAYABLE)
        {
            return payables[account.OwnerId!.Value]!.AccountId;
        }
        return await Tx.QuerySingleOrDefaultAsync<Guid?>("select id from ledger_accounts where code = @code", new { code = account.Code }, ct)
            ?? await OpenAccountAsync(account, now, ct);
    }

    private async Task<Guid> OpenAccountAsync(Account account, DateTimeOffset now, CancellationToken ct)
    {
        if (account.Kind is not AccountKind.STATION_PAYABLE and not AccountKind.COMPANY_RECEIVABLE)
        {
            throw new InvalidOperationException($"Ledger account {account.Code} does not exist");
        }
        // ON CONFLICT, not a caught duplicate-key error: in PostgreSQL a failed INSERT aborts the whole transaction, so
        // the caller's posting could not continue after catching it. When a concurrent posting opened the account a
        // moment ago, this inserts nothing and the select finds theirs. No conflict target: the code and
        // (company, kind) indexes describe the same account, and a race can trip either one first.
        await Tx.ExecuteAsync("""
            insert into ledger_accounts (id, code, kind, company_id, station_id, created_at)
            values (@id, @code, @kind, @company, @station, @now)
            on conflict do nothing
            """, new
        {
            id = Guid.CreateVersion7(now),
            code = account.Code,
            kind = account.Kind.ToString(),
            company = account.CompanyId,
            station = account.Kind == AccountKind.STATION_PAYABLE ? account.OwnerId : null,
            now = now.UtcDateTime,
        }, ct);
        return await Tx.QuerySingleAsync<Guid>("select id from ledger_accounts where code = @code", new { code = account.Code }, ct);
    }

    private async Task UpdateBalanceAsync(Guid companyId, long balance, DateTimeOffset now, CancellationToken ct)
    {
        int updated;
        try
        {
            updated = await Tx.ExecuteAsync("""
                update company_wallets set balance_piastres = @balance, updated_at = @now where company_id = @company
                """, new { balance, now = now.UtcDateTime, company = companyId }, ct);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23514")
        {
            // The CHECK (balance_piastres >= 0) is the database's own word on the same rule.
            throw new ApiException(409, InsufficientBalance, "The wallet balance is not enough");
        }
        if (updated != 1)
        {
            throw new InvalidOperationException($"Wallet {companyId} vanished while locked");
        }
    }

    private async Task UpdatePayableAsync(Guid stationId, long balance, DateTimeOffset now, CancellationToken ct)
    {
        var updated = await Tx.ExecuteAsync("""
            update station_payables set balance_piastres = @balance, updated_at = @now where station_id = @station
            """, new { balance, now = now.UtcDateTime, station = stationId }, ct);
        if (updated != 1)
        {
            throw new InvalidOperationException($"Payable of station {stationId} vanished while locked");
        }
    }

    private static ApiException AlreadyPostedError() => new(409, AlreadyPosted, "This source has already been posted");

    private static ApiException AlreadyReversedError() => new(409, AlreadyReversed, "This transaction has already been reversed");
}

/// <summary>
/// <c>java.util.UUID.compareTo</c>: the most significant 64 bits, then the least, each as a <b>signed</b> long. The
/// posting engine locks wallets (and payables) in this order, as the Java engine does, so two postings that touch the
/// same rows never wait on each other in a cycle, whichever API posts them.
/// </summary>
public sealed class JavaUuidOrder : IComparer<Guid>
{
    public static readonly JavaUuidOrder Instance = new();

    public int Compare(Guid x, Guid y)
    {
        var (xHigh, xLow) = Halves(x);
        var (yHigh, yLow) = Halves(y);
        var high = xHigh.CompareTo(yHigh);
        return high != 0 ? high : xLow.CompareTo(yLow);
    }

    private static (long High, long Low) Halves(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes[..8]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes[8..]));
    }
}

/// <summary>Timestamps read with Dapper come back as <see cref="DateTime"/>; the API speaks <see cref="DateTimeOffset"/> in UTC.</summary>
internal static class LedgerTime
{
    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    /// <summary>
    /// A date as a Dapper parameter. Dapper cannot send a <see cref="DateOnly"/>; midnight of kind Unspecified goes as a
    /// <c>timestamp</c>, which PostgreSQL's assignment cast stores in a <c>date</c> column as that same day.
    /// </summary>
    public static DateTime Date(DateOnly value) => value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
}
