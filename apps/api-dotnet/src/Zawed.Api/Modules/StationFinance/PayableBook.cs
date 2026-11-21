using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Modules.StationFinance;

/// <summary>A station's position (Java <c>PayableBook.Position</c>).</summary>
/// <param name="BalancePiastres">the payable balance; negative when the station owes the platform (a fueling reversed
/// after it was paid out)</param>
/// <param name="HeldPiastres">never negative</param>
/// <param name="PendingPayoutsPiastres">payouts recorded and waiting for a second person; always zero in an operator's
/// scope, where row-level security hides them</param>
public sealed record PayablePosition(long BalancePiastres, long HeldPiastres, long PendingPayoutsPiastres)
{
    /// <summary>May be negative: nothing can be paid until fuelings bring the balance back up.</summary>
    public long AvailablePiastres => checked(BalancePiastres - HeldPiastres);

    /// <summary>What one more payout may take.</summary>
    public long PayablePiastres => checked(AvailablePiastres - PendingPayoutsPiastres);
}

/// <summary>
/// What a station is owed, and how much of it may be paid (B6 contract §3; Java <c>PayableBook</c>).
/// <code>
/// available = payable balance − held
/// held      = amounts of offline fuelings whose payable was held at sync (B5 contract §4),
///             not yet released by Operations and not reversed
/// </code>
/// A payout that waits for approval reserves its amount: a new payout may take at most <c>available − pending
/// payouts</c>, so what is recorded can never add up to more than there is.
/// <para>The three numbers are read in <b>one statement</b>, so they belong to one snapshot. That matters for an offline
/// fueling that is credited and held in the same transaction: read apart, its credit could be seen without its hold, and
/// the station paid money that was meant to wait.</para>
/// </summary>
public sealed class PayableBook(IDbSession session)
{
    private sealed class Row
    {
        public long Balance { get; set; }
        public long Held { get; set; }
        public long Pending { get; set; }
    }

    /// <summary>
    /// The station's position now. To decide a payout, call it while holding <c>ILedger.LockPayableAsync(stationId)</c>:
    /// the balance cannot move under that lock, a hold can only be added together with its own credit, and a release or
    /// a reversal seen late only makes the answer more cautious.
    /// </summary>
    public async Task<PayablePosition> PositionAsync(Guid stationId, CancellationToken ct)
    {
        var row = await session.Current.QuerySingleAsync<Row>("""
            select coalesce((select p.balance_piastres from station_payables p where p.station_id = @station), 0) as balance,
                   (select coalesce(sum(f.amount_piastres), 0)::bigint from fuelings f
                     where f.station_id = @station and f.payable_held and f.ledger_transaction_id is not null
                       and not exists (select 1 from payable_hold_releases h where h.fueling_id = f.id)
                       and not exists (select 1 from fueling_reversals r
                                        where r.fueling_id = f.id and r.status = 'APPROVED')) as held,
                   (select coalesce(sum(o.amount_piastres), 0)::bigint from payouts o
                     where o.station_id = @station and o.status = 'PENDING') as pending
            """, new { station = stationId }, ct);
        return new PayablePosition(row.Balance, row.Held, row.Pending);
    }
}
