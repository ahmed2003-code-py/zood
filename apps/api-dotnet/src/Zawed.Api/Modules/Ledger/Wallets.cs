using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Modules.Tenancy.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Ledger;

/// <summary>
/// Company wallets as the portals read them (PRD §6.2 Corporate Wallet, §6.3 Company Profile; Java <c>Wallets</c>): the
/// balance, whether it is low, and the statement with the running balance after each entry. Opens the wallet when
/// tenancy announces a new company.
/// </summary>
public sealed class Wallets(IDbSession session, ICompanyDirectory companies, IAuditLog audit, TimeProvider clock)
    : IDomainEventHandler<CompanyCreated>
{
    private DbTx Tx => session.Current;

    private sealed class StatementRow
    {
        public Guid Id { get; set; }
        public long Seq { get; set; }
        public DateTime Posted_At { get; set; }
        public string Direction { get; set; } = "";
        public long Amount_Piastres { get; set; }
        public long Balance_After_Piastres { get; set; }
        public string Source_Type { get; set; } = "";
        public Guid Source_Id { get; set; }
        public string? Reference { get; set; }
        public string? Description { get; set; }

        public StatementEntryResponse ToResponse() => new(Id, LedgerTime.Utc(Posted_At), Enum.Parse<Direction>(Direction), Amount_Piastres,
            Balance_After_Piastres, Enum.Parse<SourceType>(Source_Type), Source_Id, Reference, Description);
    }

    /// <summary>
    /// Opens the company's wallet in the onboarding transaction (the event is synchronous). The account row is the
    /// ledger's identity for the wallet; the wallet row holds the cached balance and is what the fueling engine locks.
    /// </summary>
    public async Task HandleAsync(CompanyCreated domainEvent, CancellationToken ct)
    {
        var companyId = domainEvent.CompanyId;
        var now = clock.GetUtcNow();
        var accountId = Guid.CreateVersion7(now);
        await Tx.ExecuteAsync("""
            insert into ledger_accounts (id, code, kind, company_id, station_id, created_at)
            values (@id, @code, 'COMPANY_WALLET', @company, null, @now)
            """, new { id = accountId, code = Account.CompanyWallet(companyId).Code, company = companyId, now = now.UtcDateTime }, ct);
        await Tx.ExecuteAsync("""
            insert into company_wallets (company_id, account_id, balance_piastres, created_at, updated_at)
            values (@company, @account, 0, @now, @now)
            """, new { company = companyId, account = accountId, now = now.UtcDateTime }, ct);
        await audit.RecordAsync(Tx, AuditActor.Anonymous, "WALLET_CREATED", "COMPANY_WALLET", companyId,
            Platform.Security.Scope.Company(companyId), new Dictionary<string, object?>(), ct);
    }

    public async Task<Result<WalletResponse>> GetAsync(Guid companyId, CancellationToken ct)
    {
        var wallet = await Tx.QuerySingleOrDefaultAsync<LedgerStore.WalletRow>("""
            select company_id, account_id, balance_piastres, updated_at from company_wallets where company_id = @company
            """, new { company = companyId }, ct);
        if (wallet is null)
        {
            return Result<WalletResponse>.NotFound("Wallet");
        }
        if (await companies.FindAsync(companyId, ct) is not { } company)
        {
            return Result<WalletResponse>.NotFound("Wallet");
        }
        var threshold = company.LowBalanceThresholdPiastres;
        return new WalletResponse(companyId, wallet.Balance_Piastres, threshold, wallet.Balance_Piastres < threshold,
            LedgerTime.Utc(wallet.Updated_At));
    }

    /// <summary>
    /// The wallet's entries, newest first, each with the balance it left. Ordered and paged by the entries' insert
    /// sequence, which for one wallet is exactly the order of its balance (V6).
    /// </summary>
    public async Task<Result<Page<StatementEntryResponse>>> StatementAsync(Guid companyId, string? cursor, int? limit, CancellationToken ct)
    {
        var account = await Tx.QuerySingleOrDefaultAsync<Guid?>("select account_id from company_wallets where company_id = @company",
            new { company = companyId }, ct);
        if (account is null)
        {
            return Result<Page<StatementEntryResponse>>.NotFound("Wallet");
        }
        var size = Page<StatementEntryResponse>.Limit(limit);
        var rows = (await Tx.QueryAsync<StatementRow>("""
            select e.id, e.seq, e.posted_at, e.direction, e.amount_piastres, e.balance_after_piastres,
                   t.source_type, t.source_id, t.reference, t.description
            from ledger_entries e join ledger_transactions t on t.id = e.transaction_id
            where e.account_id = @account and (cast(@cursor as bigint) is null or e.seq < @cursor)
            order by e.seq desc limit @limit
            """, new { account, cursor = SeqCursor(cursor), limit = size + 1 }, ct)).ToList();
        if (rows.Count <= size)
        {
            return new Page<StatementEntryResponse>(rows.Select(r => r.ToResponse()).ToList(), null);
        }
        var items = rows.Take(size).ToList();
        return new Page<StatementEntryResponse>(items.Select(r => r.ToResponse()).ToList(),
            items[^1].Seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The statement's cursor is an entry sequence number, not an id (Java <c>Long.parseLong(value.strip())</c>, which
    /// reads the decimal digits of any script: <c>١٢٣</c> is 123, as harmless here as its ASCII twin).
    /// </summary>
    public static long? SeqCursor(string? value)
    {
        if (value is null || JavaText.IsBlank(value))
        {
            return null;
        }
        return JavaText.TryParseLong(JavaText.Strip(value), out var seq)
            ? seq
            : throw new ApiException(400, "INVALID_CURSOR", "Invalid cursor", "cursor");
    }
}

/// <summary><see cref="IWalletBalances"/>: the cached balances, unlocked, in the caller's scope (Java <c>LedgerWalletBalances</c>).</summary>
public sealed class LedgerWalletBalances(IDbSession session) : IWalletBalances
{
    public async Task<IReadOnlyDictionary<Guid, long>> BalancesAsync(IReadOnlyCollection<Guid> companyIds, CancellationToken ct = default)
    {
        var balances = new Dictionary<Guid, long>();
        if (companyIds.Count == 0)
        {
            return balances;
        }
        foreach (var (company, balance) in await session.Current.QueryAsync<(Guid, long)>(
            "select company_id, balance_piastres from company_wallets where company_id = any(@ids)", new { ids = companyIds.ToArray() }, ct))
        {
            balances[company] = balance;
        }
        return balances;
    }
}
