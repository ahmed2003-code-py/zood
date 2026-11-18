package com.zawed.api.stationfinance;

import java.util.UUID;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Component;

/**
 * What a station is owed, and how much of it may be paid (B6 contract §3).
 *
 * <pre>
 * available = payable balance − held
 * held      = amounts of offline fuelings whose payable was held at sync (B5 contract §4),
 *             not yet released by Operations and not reversed
 * </pre>
 *
 * A payout that waits for approval reserves its amount: a new payout may take at most
 * {@code available − pending payouts}, so what is recorded can never add up to more than there is.
 *
 * <p>The three numbers are read in <b>one statement</b>, so they belong to one snapshot. That
 * matters for an offline fueling that is credited and held in the same transaction: read apart, its
 * credit could be seen without its hold, and the station paid money that was meant to wait.
 */
@Component
class PayableBook {

	/**
	 * @param balancePiastres the payable balance; negative when the station owes the platform (a
	 * fueling reversed after it was paid out)
	 * @param heldPiastres never negative
	 * @param pendingPayoutsPiastres payouts recorded and waiting for a second person; always zero in
	 * an operator's scope, where row-level security hides them
	 */
	record Position(long balancePiastres, long heldPiastres, long pendingPayoutsPiastres) {

		/** May be negative: nothing can be paid until fuelings bring the balance back up. */
		long availablePiastres() {
			return Math.subtractExact(balancePiastres, heldPiastres);
		}

		/** What one more payout may take. */
		long payablePiastres() {
			return Math.subtractExact(availablePiastres(), pendingPayoutsPiastres);
		}

	}

	private final JdbcClient jdbc;

	PayableBook(JdbcClient jdbc) {
		this.jdbc = jdbc;
	}

	/**
	 * The station's position now. To decide a payout, call it while holding
	 * {@code Ledger.lockPayable(stationId)}: the balance cannot move under that lock, a hold can only
	 * be added together with its own credit, and a release or a reversal seen late only makes the
	 * answer more cautious.
	 */
	Position position(UUID stationId) {
		return jdbc.sql("""
				select coalesce((select p.balance_piastres from station_payables p where p.station_id = :station), 0) as balance,
				       (select coalesce(sum(f.amount_piastres), 0)::bigint from fuelings f
				         where f.station_id = :station and f.payable_held and f.ledger_transaction_id is not null
				           and not exists (select 1 from payable_hold_releases h where h.fueling_id = f.id)
				           and not exists (select 1 from fueling_reversals r
				                            where r.fueling_id = f.id and r.status = 'APPROVED')) as held,
				       (select coalesce(sum(o.amount_piastres), 0)::bigint from payouts o
				         where o.station_id = :station and o.status = 'PENDING') as pending
				""")
			.param("station", stationId)
			.query((rs, n) -> new Position(rs.getLong("balance"), rs.getLong("held"), rs.getLong("pending")))
			.single();
	}

}
