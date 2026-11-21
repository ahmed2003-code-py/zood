using System.Text;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Modules.StationFinance.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.StationFinance;

public enum BankAccountStatus
{
    /// <summary>Requested, waiting for a second person.</summary>
    PENDING,

    /// <summary>The station's verified account.</summary>
    ACTIVE,
    REJECTED,

    /// <summary>Was the verified account until a later change replaced it.</summary>
    SUPERSEDED,
}

public enum BankAccountKind
{
    IBAN,
    ACCOUNT_NUMBER,
}

/// <summary>An account as anyone may see it: the number itself is not part of the record (Java <c>BankAccounts.BankAccount</c>).</summary>
public sealed record BankAccount(Guid Id, Guid StationId, Guid OperatorId, BankAccountKind AccountKind, string AccountLast4, string BankName,
    string HolderName, string BankLetterReference, BankAccountStatus Status, Guid RequestedBy, DateTimeOffset RequestedAt, Guid? DecidedBy,
    DateTimeOffset? DecidedAt, string? DecisionReason, DateTimeOffset? PayoutsHeldUntil, DateTimeOffset? SupersededAt);

/// <summary>What a station's bank page shows: the verified account and a change that waits; either may be null.</summary>
public sealed record BankDetails(BankAccount? Active, BankAccount? Pending);

/// <summary>An account number as typed, reduced to the form that is validated, encrypted and compared.</summary>
public sealed record ParsedAccount(BankAccountKind Kind, string Normalized)
{
    public string Last4 => Normalized[^4..];

    public override string ToString() => "ParsedAccount[" + Kind + ", ****" + Last4 + "]"; // never the number
}

/// <summary>
/// Where a station is paid, under change control (roadmap delta A3: "changing the bank details is the highest-value fraud
/// path"; Java <c>BankAccounts</c>).
/// <list type="bullet">
/// <item>A station has at most one verified (<c>ACTIVE</c>) account and at most one change waiting.</item>
/// <item>A change is requested by one person, with the reference of the bank's letter, and approved by a <b>different</b>
/// person. The API refuses a self-approval and so does the database (V9).</item>
/// <item>Approval starts a payout hold: for <c>zawed.finance.bank-change-hold-hours</c> (72 by default) no payout may go
/// to the station, so a fraudulent change has to survive three days of people noticing before it can move money. The
/// first account of a station is held like any other.</item>
/// <item>The IBAN or account number is encrypted at rest, bound to its row (<see cref="Binding"/>, Java's bytes), and
/// never leaves this class in clear: responses, the audit trail and logs carry its last four characters only.</item>
/// </list>
/// Refusals are thrown as <see cref="ApiException"/>, as the Java service throws them; the request's transaction rolls back.
/// </summary>
public sealed class BankAccounts(IDbSession session, StationAccess access, ILedger ledger, DataEncryptor encryptor, IAuditLog audit,
    TimeProvider clock, StationFinanceSettings settings, IDomainEvents events)
{
    /// <summary>Every column except the encrypted number, which no read needs.</summary>
    private const string Columns = """
        id, station_id, operator_id, account_kind, account_last4, bank_name, holder_name, bank_letter_reference,
        status, requested_by, requested_at, decided_by, decided_at, decision_reason, payouts_held_until,
        superseded_at
        """;

    private DbTx Tx => session.Current;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid Station_Id { get; set; }
        public Guid Operator_Id { get; set; }
        public string Account_Kind { get; set; } = "";
        public string Account_Last4 { get; set; } = "";
        public string Bank_Name { get; set; } = "";
        public string Holder_Name { get; set; } = "";
        public string Bank_Letter_Reference { get; set; } = "";
        public string Status { get; set; } = "";
        public Guid Requested_By { get; set; }
        public DateTime Requested_At { get; set; }
        public Guid? Decided_By { get; set; }
        public DateTime? Decided_At { get; set; }
        public string? Decision_Reason { get; set; }
        public DateTime? Payouts_Held_Until { get; set; }
        public DateTime? Superseded_At { get; set; }

        public BankAccount ToAccount() => new(Id, Station_Id, Operator_Id, Enum.Parse<BankAccountKind>(Account_Kind), Account_Last4, Bank_Name,
            Holder_Name, Bank_Letter_Reference, Enum.Parse<BankAccountStatus>(Status), Requested_By, Finance.Utc(Requested_At), Decided_By,
            Finance.Utc(Decided_At), Decision_Reason, Finance.Utc(Payouts_Held_Until), Finance.Utc(Superseded_At));
    }

    // ----------------------------------------------------------------------------------------------------- queries

    public async Task<BankDetails> DetailsAsync(ZawedPrincipal principal, Guid stationId, CancellationToken ct)
    {
        await access.RequireAsync(principal, stationId, ct);
        return new BankDetails(await ByStatusAsync(stationId, BankAccountStatus.ACTIVE, ct), await ByStatusAsync(stationId, BankAccountStatus.PENDING, ct));
    }

    /// <summary>The change queue and its history, newest first.</summary>
    public async Task<Page<BankAccount>> ChangesAsync(BankAccountStatus? status, Guid? stationId, Guid? operatorId, string? cursor, int? limit,
        CancellationToken ct)
    {
        var size = Page<BankAccount>.Limit(limit);
        var rows = await Tx.QueryAsync<Row>("select " + Columns + """
             from station_bank_accounts
            where (cast(@status as text) is null or status = @status)
              and (cast(@station as uuid) is null or station_id = @station)
              and (cast(@operator as uuid) is null or operator_id = @operator)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new { status = status?.ToString(), station = stationId, @operator = operatorId, cursor = Cursors.Parse(cursor), limit = size + 1 }, ct);
        return Page<BankAccount>.Of(rows.Select(r => r.ToAccount()).ToList(), size, a => a.Id);
    }

    /// <summary>The station's verified account, if it has one. Runs in the caller's transaction.</summary>
    public Task<BankAccount?> ActiveAsync(Guid stationId, CancellationToken ct) => ByStatusAsync(stationId, BankAccountStatus.ACTIVE, ct);

    // ---------------------------------------------------------------------------------------------- change control

    /// <summary>Step one: Operations asks for the station to be paid into this account from now on.</summary>
    public async Task<BankAccount> RequestChangeAsync(ZawedPrincipal actor, Guid stationId, string accountNumber, string bankName,
        string holderName, string bankLetterReference, CancellationToken ct)
    {
        var station = await access.RequireAsync(actor, stationId, ct);
        var account = await ParseAsync(Tx, accountNumber, ct);
        if (await ByStatusAsync(stationId, BankAccountStatus.PENDING, ct) is not null)
        {
            throw ChangePending();
        }
        var now = Finance.Now(clock);
        var id = Guid.CreateVersion7(now);
        try
        {
            await Tx.ExecuteAsync("""
                insert into station_bank_accounts (id, station_id, operator_id, account_kind, account_encrypted,
                                                   account_last4, bank_name, holder_name, bank_letter_reference, status,
                                                   requested_by, requested_at, created_at, updated_at)
                values (@id, @station, @operator, @kind, @encrypted, @last4, @bank, @holder, @letter, 'PENDING', @actor,
                        @now, @now, @now)
                """, new
            {
                id,
                station = station.Id,
                @operator = station.OperatorId,
                kind = account.Kind.ToString(),
                encrypted = encryptor.Encrypt(Encoding.UTF8.GetBytes(account.Normalized), Binding(id)),
                last4 = account.Last4,
                bank = Sql.Clean(bankName),
                holder = Sql.Clean(holderName),
                letter = Sql.Clean(bankLetterReference),
                actor = actor.UserId,
                now = now.UtcDateTime,
            }, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw ChangePending(); // a concurrent request for the same station won the unique index
        }
        await audit.RecordAsync(Tx, Finance.ActorOf(actor), "BANK_ACCOUNT_CHANGE_REQUESTED", "STATION_BANK_ACCOUNT", id,
            Scope.Operator(station.OperatorId), new Dictionary<string, object?>
            {
                ["stationId"] = station.Id.ToString(),
                ["accountKind"] = account.Kind.ToString(),
                ["accountLast4"] = account.Last4,
                ["bankName"] = Sql.Clean(bankName),
                ["bankLetterReference"] = Sql.Clean(bankLetterReference),
            }, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Step two, by a different person: the account becomes the station's verified one, the previous one is superseded,
    /// and the payout hold starts.
    /// </summary>
    public async Task<BankAccount> ApproveAsync(ZawedPrincipal actor, Guid id, CancellationToken ct)
    {
        var change = await LockAsync(id, ct);
        RequirePending(change);
        if (change.RequestedBy == actor.UserId)
        {
            throw Finance.SelfApproval();
        }
        // The payable lock is what a payout to this station holds while it checks the account and the hold (Payouts.Record,
        // Payouts.Approve). Taking it here puts the two in one order: a payout either committed before this change, to the
        // account that was verified then, or starts after it and meets the hold. Lock order: the change row, then the
        // payable; a payout never locks a bank account row, so the two cannot wait on each other.
        await ledger.LockPayableAsync(change.StationId, ct);
        var now = Finance.Now(clock);
        var heldUntil = now + settings.Hold;
        var replaced = await Tx.QuerySingleOrDefaultAsync<Guid?>("""
            update station_bank_accounts set status = 'SUPERSEDED', superseded_at = @now, updated_at = @now
            where station_id = @station and status = 'ACTIVE' returning id
            """, new { station = change.StationId, now = now.UtcDateTime }, ct);
        var updated = await Tx.ExecuteAsync("""
            update station_bank_accounts set status = 'ACTIVE', decided_by = @actor, decided_at = @now,
                   payouts_held_until = @heldUntil, updated_at = @now
            where id = @id and status = 'PENDING'
            """, new { actor = actor.UserId, now = now.UtcDateTime, heldUntil = heldUntil.UtcDateTime, id }, ct);
        if (updated != 1)
        {
            throw NotPending(change.Status);
        }
        var facts = new Dictionary<string, object?>
        {
            ["stationId"] = change.StationId.ToString(),
            ["accountLast4"] = change.AccountLast4,
            ["requestedBy"] = change.RequestedBy.ToString(),
            ["payoutsHeldUntil"] = Finance.JavaInstant(heldUntil),
        };
        if (replaced is { } previous)
        {
            facts["replacedAccountId"] = previous.ToString();
        }
        await audit.RecordAsync(Tx, Finance.ActorOf(actor), "BANK_ACCOUNT_CHANGE_APPROVED", "STATION_BANK_ACCOUNT", id,
            Scope.Operator(change.OperatorId), facts, ct);
        await events.PublishAsync(new BankAccountChangeDecided(id, change.OperatorId, change.StationId, true), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>The change is refused, by anyone who may decide, the requester included: nothing becomes payable.</summary>
    public async Task<BankAccount> RejectAsync(ZawedPrincipal actor, Guid id, string reason, CancellationToken ct)
    {
        var change = await LockAsync(id, ct);
        RequirePending(change);
        var now = Finance.Now(clock);
        var updated = await Tx.ExecuteAsync("""
            update station_bank_accounts set status = 'REJECTED', decided_by = @actor, decided_at = @now,
                   decision_reason = @reason, updated_at = @now
            where id = @id and status = 'PENDING'
            """, new { actor = actor.UserId, now = now.UtcDateTime, reason, id }, ct);
        if (updated != 1)
        {
            throw NotPending(change.Status);
        }
        await audit.RecordAsync(Tx, Finance.ActorOf(actor), "BANK_ACCOUNT_CHANGE_REJECTED", "STATION_BANK_ACCOUNT", id,
            Scope.Operator(change.OperatorId), new Dictionary<string, object?>
            {
                ["stationId"] = change.StationId.ToString(),
                ["accountLast4"] = change.AccountLast4,
                ["reason"] = reason,
            }, ct);
        await events.PublishAsync(new BankAccountChangeDecided(id, change.OperatorId, change.StationId, false), ct);
        return await GetAsync(id, ct);
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    public async Task<BankAccount> GetAsync(Guid id, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("select " + Columns + " from station_bank_accounts where id = @id", new { id }, ct))?.ToAccount()
        ?? throw ApiException.NotFound("Bank account change");

    private async Task<BankAccount?> ByStatusAsync(Guid stationId, BankAccountStatus status, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("select " + Columns + " from station_bank_accounts where station_id = @station and status = @status",
            new { station = stationId, status = status.ToString() }, ct))?.ToAccount();

    /// <summary>Reads the change and holds its row until commit, so two decisions never both see PENDING.</summary>
    private async Task<BankAccount> LockAsync(Guid id, CancellationToken ct) =>
        (await Tx.QuerySingleOrDefaultAsync<Row>("select " + Columns + " from station_bank_accounts where id = @id for update", new { id }, ct))
        ?.ToAccount() ?? throw ApiException.NotFound("Bank account change");

    private static void RequirePending(BankAccount change)
    {
        if (change.Status != BankAccountStatus.PENDING)
        {
            throw NotPending(change.Status);
        }
    }

    /// <summary>
    /// Recognises and checks what was typed (Java <c>BankAccounts.parse</c>). Spaces, dashes and invisible marks are
    /// dropped and digits of any script folded, exactly as for a bank reference. Two leading letters mean an IBAN, which
    /// must have the standard shape and pass its mod-97 check (an Egyptian one is 29 characters); anything else must be a
    /// plain account number of 6 to 34 digits. <c>400 INVALID_BANK_ACCOUNT</c>; the message never repeats the input.
    /// </summary>
    public static async Task<ParsedAccount> ParseAsync(DbTx tx, string? accountNumber, CancellationToken ct)
    {
        var value = await BankReferences.KeyAsync(tx, Storable(accountNumber ?? ""), ct);
        if (value.Length >= 2 && IsLetter(value[0]) && IsLetter(value[1]))
        {
            var shaped = value.Length is >= 15 and <= 34 && IsDigit(value[2]) && IsDigit(value[3])
                && (!value.StartsWith("EG", StringComparison.Ordinal) || value.Length == 29);
            if (!shaped || Mod97(value[4..] + value[..4]) != 1)
            {
                throw InvalidAccount();
            }
            return new ParsedAccount(BankAccountKind.IBAN, value);
        }
        if (value.Length is < 6 or > 34 || !value.All(IsDigit))
        {
            throw InvalidAccount();
        }
        return new ParsedAccount(BankAccountKind.ACCOUNT_NUMBER, value);
    }

    /// <summary>
    /// Java folds a NUL or a lone surrogate out of the key like any other mark, where <see cref="BankReferences.KeyAsync"/>
    /// refuses a reference PostgreSQL cannot hold. Each becomes a space: like them a starter (it blocks NFKC composition
    /// the same way) and dropped by the fold, so the key is Java's.
    /// </summary>
    private static string Storable(string text)
    {
        StringBuilder? copy = null;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsSurrogatePair(text, i))
            {
                copy?.Append(text, i, 2);
                i++;
                continue;
            }
            var bad = text[i] == '\0' || char.IsSurrogate(text[i]);
            if (bad && copy is null)
            {
                copy = new StringBuilder(text, 0, i, text.Length);
            }
            copy?.Append(bad ? ' ' : text[i]);
        }
        return copy?.ToString() ?? text;
    }

    /// <summary>ISO 13616: letters count as 10 to 35; the number is far too long for a long, so it is reduced as it is read.</summary>
    private static int Mod97(string rearranged)
    {
        var remainder = 0;
        foreach (var c in rearranged)
        {
            remainder = IsDigit(c) ? (remainder * 10 + (c - '0')) % 97 : (remainder * 100 + (c - 'A' + 10)) % 97;
        }
        return remainder;
    }

    private static bool IsLetter(char c) => c is >= 'A' and <= 'Z';

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    /// <summary>
    /// Binds the encrypted number to its row: copied into another row, it does not decrypt. Java's
    /// <c>ByteBuffer.allocate(20).put("sbac").putLong(msb).putLong(lsb)</c>: the ASCII tag, then the UUID's 16 bytes in
    /// big-endian (RFC 4122) order.
    /// </summary>
    public static byte[] Binding(Guid accountId)
    {
        var binding = new byte[20];
        "sbac"u8.CopyTo(binding);
        accountId.TryWriteBytes(binding.AsSpan(4), bigEndian: true, out _);
        return binding;
    }

    private static ApiException InvalidAccount() => new(400, "INVALID_BANK_ACCOUNT",
        "Enter a valid IBAN, or an account number of 6 to 34 digits", "accountNumber");

    private static ApiException ChangePending() => new(409, "BANK_ACCOUNT_CHANGE_PENDING",
        "A bank account change for this station is already waiting for approval");

    private static ApiException NotPending(BankAccountStatus status) => new(409, "BANK_ACCOUNT_CHANGE_NOT_PENDING",
        "The change has already been decided", null, new Dictionary<string, object> { ["status"] = status.ToString() });
}
