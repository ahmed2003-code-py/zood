using System.Globalization;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.StationFinance;

/// <summary>The fueling behind a <c>FUELING</c> entry, or the fueling a <c>REVERSAL</c> entry reverses.</summary>
public sealed record FuelingLink(Guid Id, string Reference, string Plate, bool PayableHeld, bool HoldReleased);

/// <summary>The payout behind a <c>PAYOUT</c> entry.</summary>
public sealed record PayoutLink(Guid Id, string BankReference, DateOnly ValueDate);

/// <summary>One entry of a station statement (Java <c>StationStatements.Line</c>).</summary>
/// <param name="Direction">CREDIT raises what the station is owed (a fueling), DEBIT lowers it (a payout, a reversal)</param>
/// <param name="BalanceAfterPiastres">the payable right after this entry; negative when the station owes</param>
public sealed record StatementLine(Guid Id, long Seq, DateTimeOffset PostedAt, Direction Direction, long AmountPiastres,
    long BalanceAfterPiastres, SourceType SourceType, Guid SourceId, string? Reference, string? Description, FuelingLink? Fueling,
    PayoutLink? Payout);

/// <summary>
/// A period of the statement. <c>closing = opening + credits − debits</c>, each a plain sum of the account's entries by
/// their posting time: <c>[from, to)</c>.
/// </summary>
/// <param name="Position">the station's position now (not at the period's end): balance, held, pending</param>
public sealed record StatementPeriod(Guid StationId, DateTimeOffset? From, DateTimeOffset? To, long OpeningPiastres, long CreditsPiastres,
    long DebitsPiastres, long ClosingPiastres, int Entries, PayablePosition Position);

/// <summary>
/// The station statement (roadmap delta A1, appendix D; Java <c>StationStatements</c>): the entries of the station's
/// payable account in the ledger, newest first, each with the balance it left and a link to what caused it, and a
/// summary of any period. There is no separate station ledger: this is a reading of the one ledger, so it agrees with
/// it to the piastre by construction.
/// <para>Order and paging are by the entries' insert sequence. Every posting to a payable is made under that payable's
/// lock (ADR 0020), so for one station the sequence is exactly the order of its balance, as it is for a wallet (ADR
/// 0017).</para>
/// </summary>
public sealed class StationStatements(IDbSession session, StationAccess access, PayableBook book)
{
    private DbTx Tx => session.Current;

    private sealed class LineRow
    {
        public Guid Id { get; set; }
        public long Seq { get; set; }
        public DateTime Posted_At { get; set; }
        public string Direction { get; set; } = "";
        public long Amount_Piastres { get; set; }
        public long Balance_After { get; set; }
        public string Source_Type { get; set; } = "";
        public Guid Source_Id { get; set; }
        public string? Reference { get; set; }
        public string? Description { get; set; }
        public Guid? Fueling_Id { get; set; }
        public string? Fueling_Reference { get; set; }
        public string? Fueling_Plate { get; set; }
        public bool Fueling_Held { get; set; }
        public bool Hold_Released { get; set; }
        public Guid? Payout_Id { get; set; }
        public string? Payout_Reference { get; set; }
        public DateOnly? Payout_Value_Date { get; set; }

        public StatementLine ToLine() => new(Id, Seq, Finance.Utc(Posted_At), Enum.Parse<Direction>(Direction), Amount_Piastres, Balance_After,
            Enum.Parse<SourceType>(Source_Type), Source_Id, Reference, Description,
            Fueling_Id is { } fueling ? new FuelingLink(fueling, Fueling_Reference!, Fueling_Plate!, Fueling_Held, Hold_Released) : null,
            Payout_Id is { } payout ? new PayoutLink(payout, Payout_Reference!, Payout_Value_Date!.Value) : null);
    }

    private sealed class PeriodRow
    {
        public long Opening { get; set; }
        public long Credits { get; set; }
        public long Debits { get; set; }
        public long Entries { get; set; }
    }

    /// <summary>What the station is owed now, what of it is held, and what may be paid.</summary>
    public async Task<PayablePosition> PositionAsync(ZawedPrincipal principal, Guid stationId, CancellationToken ct)
    {
        await access.RequireAsync(principal, stationId, ct);
        return await book.PositionAsync(stationId, ct);
    }

    /// <summary>The payable's entries, newest first, each with the balance it left.</summary>
    public async Task<Page<StatementLine>> StatementAsync(ZawedPrincipal principal, Guid stationId, string? cursor, int? limit, CancellationToken ct)
    {
        await access.RequireAsync(principal, stationId, ct);
        var size = Page<StatementLine>.Limit(limit);
        if (await AccountAsync(stationId, ct) is not { } account)
        {
            return new Page<StatementLine>([], null); // never posted to: an empty statement, not an error
        }
        // balance_after is recorded on every payable entry since V9. An entry written before (B4, B5) has none; its
        // balance is the sum of the account's entries up to it, which is the same number.
        var rows = (await Tx.QueryAsync<LineRow>("""
            select e.id, e.seq, e.posted_at, e.direction, e.amount_piastres,
                   coalesce(e.balance_after_piastres,
                            (select sum(case x.direction when 'CREDIT' then x.amount_piastres else -x.amount_piastres end)
                               from ledger_entries x where x.account_id = e.account_id and x.seq <= e.seq))::bigint
                     as balance_after,
                   t.source_type, t.source_id, t.reference, t.description,
                   f.id as fueling_id, f.reference as fueling_reference, f.plate as fueling_plate,
                   coalesce(f.payable_held, false) as fueling_held,
                   exists (select 1 from payable_hold_releases h where h.fueling_id = f.id) as hold_released,
                   p.id as payout_id, p.bank_reference as payout_reference, p.value_date as payout_value_date
            from ledger_entries e
            join ledger_transactions t on t.id = e.transaction_id
            left join fueling_reversals r on t.source_type = 'REVERSAL' and r.id = t.source_id
            left join fuelings f on f.id = case t.source_type when 'FUELING' then t.source_id
                                                             when 'REVERSAL' then r.fueling_id end
            left join payouts p on t.source_type = 'PAYOUT' and p.id = t.source_id
            where e.account_id = @account and (cast(@cursor as bigint) is null or e.seq < @cursor)
            order by e.seq desc limit @limit
            """, new { account, cursor = Finance.SeqCursor(cursor), limit = size + 1 }, ct)).Select(r => r.ToLine()).ToList();
        if (rows.Count <= size)
        {
            return new Page<StatementLine>(rows, null);
        }
        var items = rows.Take(size).ToList();
        return new Page<StatementLine>(items, items[^1].Seq.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Opening balance, credits, debits and closing balance of <c>[from, to)</c>, with the station's position now. A null
    /// <paramref name="from"/> starts at the first entry; a null <paramref name="to"/> ends at the last.
    /// </summary>
    public async Task<StatementPeriod> PeriodAsync(ZawedPrincipal principal, Guid stationId, DateTimeOffset? from, DateTimeOffset? to,
        CancellationToken ct)
    {
        await access.RequireAsync(principal, stationId, ct);
        if (from is { } f && to is { } t && f >= t)
        {
            throw new ApiException(400, "INVALID_PERIOD", "The period must start before it ends", "from");
        }
        var position = await book.PositionAsync(stationId, ct);
        if (await AccountAsync(stationId, ct) is not { } account)
        {
            return new StatementPeriod(stationId, from, to, 0, 0, 0, 0, 0, position);
        }
        // One statement, one snapshot: the four numbers always add up, whatever is posted meanwhile.
        var row = await Tx.QuerySingleAsync<PeriodRow>("""
            select coalesce(sum(case e.direction when 'CREDIT' then e.amount_piastres else -e.amount_piastres end)
                              filter (where e.posted_at < cast(@from as timestamptz)), 0)::bigint as opening,
                   coalesce(sum(e.amount_piastres) filter (where e.direction = 'CREDIT'
                              and (cast(@from as timestamptz) is null or e.posted_at >= @from)), 0)::bigint as credits,
                   coalesce(sum(e.amount_piastres) filter (where e.direction = 'DEBIT'
                              and (cast(@from as timestamptz) is null or e.posted_at >= @from)), 0)::bigint as debits,
                   count(*) filter (where cast(@from as timestamptz) is null or e.posted_at >= @from) as entries
            from ledger_entries e
            where e.account_id = @account and (cast(@to as timestamptz) is null or e.posted_at < @to)
            """, new { account, from = from?.UtcDateTime, to = to?.UtcDateTime }, ct);
        return new StatementPeriod(stationId, from, to, row.Opening, row.Credits, row.Debits, checked(row.Opening + row.Credits - row.Debits),
            checked((int)row.Entries), position);
    }

    /// <summary>The station's payable account, once anything was posted to it.</summary>
    private Task<Guid?> AccountAsync(Guid stationId, CancellationToken ct) =>
        Tx.QuerySingleOrDefaultAsync<Guid?>("select account_id from station_payables where station_id = @station", new { station = stationId }, ct);
}
