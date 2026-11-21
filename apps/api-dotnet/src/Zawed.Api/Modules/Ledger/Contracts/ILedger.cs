using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Ledger.Contracts;

/// <summary>What caused a posting. One source id posts at most once per type.</summary>
public enum SourceType
{
    /// <summary>A confirmed top-up by bank transfer: <c>platform:bank</c> to the company wallet.</summary>
    TOPUP,

    /// <summary>
    /// A billable fueling: company wallet to the station payable (B4). An offline fueling the wallet cannot cover
    /// debits the wallet what it holds and the company receivable the rest (B5).
    /// </summary>
    FUELING,

    /// <summary>A station payout (B6): station payable to <c>platform:bank</c>.</summary>
    PAYOUT,

    /// <summary>The mirror of an earlier transaction (B6, two-person rule); posted with <see cref="ILedger.ReverseAsync"/>.</summary>
    REVERSAL,

    /// <summary>
    /// An approved wallet adjustment (V12, ADR 0021): <c>platform:adjustments</c> to the company wallet, after two
    /// different people agreed. The B3 tests also use it to debit a wallet.
    /// </summary>
    ADJUSTMENT,
}

public enum Direction
{
    DEBIT,
    CREDIT,
}

/// <summary>The kinds of named account of plan §3.5.</summary>
public enum AccountKind
{
    PLATFORM_BANK,

    /// <summary>
    /// The counter-account of approved wallet credits that are not bank transfers (V12, ADR 0021): its balance is the
    /// total ever granted by adjustment, kept apart from real bank money.
    /// </summary>
    PLATFORM_ADJUSTMENT,
    COMPANY_WALLET,

    /// <summary>
    /// What a company owes for fuel it took while its wallet could not cover it (B5 offline sync, ADR 0019). An asset
    /// of the platform: a debit raises the debt, a credit settles it. It has no cached balance, no lock and no floor:
    /// those belong to the wallet alone.
    /// </summary>
    COMPANY_RECEIVABLE,
    STATION_PAYABLE,
}

/// <summary>A named account of plan §3.5 (Java <c>Ledger.Account</c>).</summary>
public sealed record Account
{
    public Account(AccountKind kind, Guid? ownerId)
    {
        if (IsPlatformKind(kind) != (ownerId is null))
        {
            throw new ArgumentException(kind + (ownerId is null ? " needs an owner" : " has no owner"));
        }
        Kind = kind;
        OwnerId = ownerId;
    }

    public AccountKind Kind { get; }

    public Guid? OwnerId { get; }

    /// <summary>The platform's own accounts, which have no owner.</summary>
    public static bool IsPlatformKind(AccountKind kind) => kind is AccountKind.PLATFORM_BANK or AccountKind.PLATFORM_ADJUSTMENT;

    public static Account PlatformBank() => new(AccountKind.PLATFORM_BANK, null);

    public static Account PlatformAdjustments() => new(AccountKind.PLATFORM_ADJUSTMENT, null);

    public static Account CompanyWallet(Guid companyId) => new(AccountKind.COMPANY_WALLET, companyId);

    public static Account CompanyReceivable(Guid companyId) => new(AccountKind.COMPANY_RECEIVABLE, companyId);

    public static Account StationPayable(Guid stationId) => new(AccountKind.STATION_PAYABLE, stationId);

    /// <summary>
    /// The company whose money the account holds, or null for the bank and a station's payable. Entries on a
    /// company's account carry it (the database checks they agree, V6).
    /// </summary>
    public Guid? CompanyId => Kind is AccountKind.COMPANY_WALLET or AccountKind.COMPANY_RECEIVABLE ? OwnerId : null;

    /// <summary>
    /// The account's code as stored: <c>platform:bank</c>, <c>platform:adjustments</c>, <c>company:{id}:wallet</c>,
    /// <c>company:{id}:receivable</c>, <c>station:{id}:payable</c>.
    /// </summary>
    public string Code => Kind switch
    {
        AccountKind.PLATFORM_BANK => "platform:bank",
        AccountKind.PLATFORM_ADJUSTMENT => "platform:adjustments",
        AccountKind.COMPANY_WALLET => $"company:{OwnerId}:wallet",
        AccountKind.COMPANY_RECEIVABLE => $"company:{OwnerId}:receivable",
        _ => $"station:{OwnerId}:payable",
    };
}

/// <summary>One side of a posting. The amount is always positive; the direction says which side.</summary>
public sealed record Entry
{
    public Entry(Account account, Direction direction, long piastres)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (piastres <= 0)
        {
            throw new ArgumentException("An entry amount must be positive: " + piastres);
        }
        Account = account;
        Direction = direction;
        Piastres = piastres;
    }

    public Account Account { get; }

    public Direction Direction { get; }

    public long Piastres { get; }

    public static Entry Debit(Account account, long piastres) => new(account, Direction.DEBIT, piastres);

    public static Entry Credit(Account account, long piastres) => new(account, Direction.CREDIT, piastres);
}

/// <summary>Who posts: a signed-in user, or the system (a job, later a device) with no user.</summary>
public sealed record Poster(Guid? UserId, Role? Role)
{
    public static readonly Poster System = new(null, null);

    public static Poster Of(ZawedPrincipal principal) => new(principal.UserId, principal.Role);
}

/// <summary>A balanced transaction to post (Java <c>Ledger.Posting</c>).</summary>
public sealed record Posting
{
    /// <param name="reference">what a reader looks for on a statement: a bank reference, a receipt reference</param>
    /// <param name="description">a short English fallback; the UI words the entry from the source type</param>
    public Posting(SourceType sourceType, Guid sourceId, string? reference, string? description, Poster poster, IEnumerable<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(poster);
        var list = entries.ToList();
        if (list.Count < 2)
        {
            throw new ArgumentException("A posting needs at least two entries");
        }
        long debits = 0;
        long credits = 0;
        foreach (var entry in list)
        {
            if (entry.Direction == Direction.DEBIT)
            {
                debits = checked(debits + entry.Piastres);
            }
            else
            {
                credits = checked(credits + entry.Piastres);
            }
        }
        if (debits != credits)
        {
            throw new ArgumentException($"A posting must balance: debits {debits} <> credits {credits}");
        }
        SourceType = sourceType;
        SourceId = sourceId;
        Reference = reference;
        Description = description;
        Poster = poster;
        Entries = list.AsReadOnly();
    }

    public SourceType SourceType { get; }

    public Guid SourceId { get; }

    public string? Reference { get; }

    public string? Description { get; }

    public Poster Poster { get; }

    public IReadOnlyList<Entry> Entries { get; }
}

/// <summary>A company wallet's state while its row is locked.</summary>
public sealed record Wallet(Guid CompanyId, Guid AccountId, long BalancePiastres, DateTimeOffset UpdatedAt);

/// <summary>
/// A station payable's state while its row is locked: what the platform owes the station. Negative when the station
/// owes the platform.
/// </summary>
public sealed record Payable(Guid StationId, Guid AccountId, long BalancePiastres, DateTimeOffset UpdatedAt);

/// <summary>How a posting moved one wallet.</summary>
public sealed record WalletChange(Guid CompanyId, long BalanceBefore, long BalanceAfter, long LowBalanceThreshold)
{
    /// <summary>True when this posting took the wallet from at-or-above its threshold to below it.</summary>
    public bool CrossedBelowThreshold => BalanceBefore >= LowBalanceThreshold && BalanceAfter < LowBalanceThreshold;

    public bool IsLow => BalanceAfter < LowBalanceThreshold;
}

/// <summary>How a posting moved one station payable.</summary>
public sealed record PayableChange(Guid StationId, long BalanceBefore, long BalanceAfter);

/// <param name="Wallets">one change per wallet the posting touched, in lock order; empty when it touched none (an
/// offline fueling charged wholly to the receivable)</param>
/// <param name="Payables">one change per station payable the posting touched, in lock order</param>
public sealed record Posted(Guid TransactionId, IReadOnlyList<WalletChange> Wallets, IReadOnlyList<PayableChange> Payables);

/// <summary>
/// The mirror of an earlier transaction (PRD §6.4): every account the original debited is credited the same amount
/// and the reverse, so the two cancel out exactly.
/// </summary>
/// <param name="ReversalId">the source of the posting: the reversal record that was approved</param>
/// <param name="OriginalTransactionId">the ledger transaction to mirror</param>
public sealed record Reversal(Guid ReversalId, Guid OriginalTransactionId, string? Reference, string? Description, Poster Poster);

/// <summary>
/// The only way money moves (implementation plan §2, boundary rule 1; Java <c>ledger.Ledger</c>). Other modules call it
/// from inside their own transaction (<c>IDbSession.Current</c>): a top-up confirmation, a fueling (B4), a payout (B6)
/// post here and commit or roll back together with their own rows.
/// <para>Amounts are integer piastres. A posting is double-entry and balanced; the database checks it again at commit.
/// A source posts once: a second posting of the same <c>(sourceType, sourceId)</c> answers <c>409 ALREADY_POSTED</c> and
/// writes nothing.</para>
/// <para>A company wallet's cached balance changes only here, under the wallet row's lock, in the same transaction as
/// the entry that changes it. Lock order is wallet first (then the tag, in the fueling engine): callers that lock the
/// wallet themselves with <see cref="LockWalletAsync"/> before posting take the same lock, so the posting never waits
/// on them.</para>
/// <para>A station payable works the same way since B6 (ADR 0020): its cached balance changes only here, under the
/// payable row's lock, and every payable entry records the balance it leaves. A payable is locked <b>after</b> every
/// wallet the posting touches, never before: wallet, (vehicle, tag,) payable. A payout locks only the payable; a
/// fueling and a reversal lock the wallet first. Unlike a wallet, a payable has no floor.</para>
/// <para>Refusals are thrown as <c>ApiException</c>, as in Java: the caller's transaction then rolls back.</para>
/// </summary>
public interface ILedger
{
    /// <summary>
    /// Reads the company's wallet and holds its row until the transaction ends. 404 when the company has no wallet in
    /// the caller's scope.
    /// </summary>
    Task<Wallet> LockWalletAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>
    /// Reads the station's payable and holds its row until the transaction ends, opening it (at zero) when the station
    /// has never been posted to. In the platform scope. Take it after any wallet lock, never before. 404 when there
    /// is no such station.
    /// </summary>
    Task<Payable> LockPayableAsync(Guid stationId, CancellationToken ct = default);

    /// <summary>
    /// Posts a balanced transaction, locking every wallet and then every station payable it touches, updating each
    /// cached balance and writing the entries, all in the caller's transaction.
    /// <c>409 ALREADY_POSTED</c> for a source that has posted before; <c>409 INSUFFICIENT_BALANCE</c> when a wallet
    /// debit would take its balance below zero; 404 when a wallet is not in the caller's scope;
    /// <see cref="ArgumentException"/> for a <see cref="SourceType.REVERSAL"/> (use <see cref="ReverseAsync"/>).
    /// </summary>
    Task<Posted> PostAsync(Posting posting, CancellationToken ct = default);

    /// <summary>
    /// Posts the exact mirror of an earlier transaction as a <see cref="SourceType.REVERSAL"/>, with the same locks and
    /// guarantees as <see cref="PostAsync"/>. A transaction is reversed at most once, and a reversal is never reversed;
    /// the database checks both (V9). <c>409 ALREADY_REVERSED</c>, <c>409 ALREADY_POSTED</c>, or 404 when the original
    /// does not exist.
    /// </summary>
    Task<Posted> ReverseAsync(Reversal reversal, CancellationToken ct = default);
}

/// <summary>
/// Cached wallet balances for reading, without locking (the POS snapshot, B5 contract §2; Java <c>WalletBalances</c>).
/// Never use these to decide a posting: decide under <see cref="ILedger.LockWalletAsync"/>. Runs in the caller's scope.
/// </summary>
public interface IWalletBalances
{
    /// <summary>Balance in piastres for each of these companies that has a wallet visible to the caller.</summary>
    Task<IReadOnlyDictionary<Guid, long>> BalancesAsync(IReadOnlyCollection<Guid> companyIds, CancellationToken ct = default);
}
