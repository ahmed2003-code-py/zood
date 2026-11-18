package com.zawed.api.stationfinance;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.Date;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Instant;
import java.time.LocalDate;
import java.util.List;
import java.util.Optional;
import java.util.UUID;

import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.ledger.Ledger.Direction;
import com.zawed.api.ledger.Ledger.SourceType;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;
import com.zawed.api.stationfinance.PayableBook.Position;

/**
 * The station statement (roadmap delta A1, appendix D): the entries of the station's payable
 * account in the ledger, newest first, each with the balance it left and a link to what caused it,
 * and a summary of any period. There is no separate station ledger: this is a reading of the one
 * ledger, so it agrees with it to the piastre by construction.
 *
 * <p>Order and paging are by the entries' insert sequence. Every posting to a payable is made under
 * that payable's lock (ADR 0020), so for one station the sequence is exactly the order of its
 * balance, as it is for a wallet (ADR 0017).
 */
@Service
class StationStatements {

	/** The fueling behind a {@code FUELING} entry, or the fueling a {@code REVERSAL} entry reverses. */
	record FuelingLink(UUID id, String reference, String plate, boolean payableHeld, boolean holdReleased) {
	}

	/** The payout behind a {@code PAYOUT} entry. */
	record PayoutLink(UUID id, String bankReference, LocalDate valueDate) {
	}

	/**
	 * @param direction CREDIT raises what the station is owed (a fueling), DEBIT lowers it (a payout,
	 * a reversal)
	 * @param balanceAfterPiastres the payable right after this entry; negative when the station owes
	 */
	record Line(UUID id, long seq, Instant postedAt, Direction direction, long amountPiastres,
			long balanceAfterPiastres, SourceType sourceType, UUID sourceId, String reference, String description,
			FuelingLink fueling, PayoutLink payout) {

		static Line map(ResultSet rs, int row) throws SQLException {
			UUID fuelingId = rs.getObject("fueling_id", UUID.class);
			UUID payoutId = rs.getObject("payout_id", UUID.class);
			Date valueDate = rs.getDate("payout_value_date");
			return new Line(rs.getObject("id", UUID.class), rs.getLong("seq"), instant(rs, "posted_at"),
					Direction.valueOf(rs.getString("direction")), rs.getLong("amount_piastres"),
					rs.getLong("balance_after"), SourceType.valueOf(rs.getString("source_type")),
					rs.getObject("source_id", UUID.class), rs.getString("reference"), rs.getString("description"),
					fuelingId == null ? null
							: new FuelingLink(fuelingId, rs.getString("fueling_reference"), rs.getString("fueling_plate"),
									rs.getBoolean("fueling_held"), rs.getBoolean("hold_released")),
					payoutId == null ? null
							: new PayoutLink(payoutId, rs.getString("payout_reference"),
									valueDate == null ? null : valueDate.toLocalDate()));
		}

	}

	/**
	 * A period of the statement. {@code closing = opening + credits − debits}, each a plain sum of
	 * the account's entries by their posting time: {@code [from, to)}.
	 *
	 * @param position the station's position now (not at the period's end): balance, held, pending
	 */
	record Period(UUID stationId, Instant from, Instant to, long openingPiastres, long creditsPiastres,
			long debitsPiastres, long closingPiastres, int entries, Position position) {
	}

	private final JdbcClient jdbc;
	private final StationAccess access;
	private final PayableBook book;

	StationStatements(JdbcClient jdbc, StationAccess access, PayableBook book) {
		this.jdbc = jdbc;
		this.access = access;
		this.book = book;
	}

	/** What the station is owed now, what of it is held, and what may be paid. */
	@Transactional(readOnly = true)
	Position position(ZawedPrincipal principal, UUID stationId) {
		access.require(principal, stationId);
		return book.position(stationId);
	}

	/** The payable's entries, newest first, each with the balance it left. */
	@Transactional(readOnly = true)
	Page<Line> statement(ZawedPrincipal principal, UUID stationId, String cursor, Integer limit) {
		access.require(principal, stationId);
		int size = Page.limit(limit);
		Optional<UUID> account = account(stationId);
		if (account.isEmpty()) {
			return new Page<>(List.of(), null); // never posted to: an empty statement, not an error
		}
		// balance_after is recorded on every payable entry since V9. An entry written before (B4, B5)
		// has none; its balance is the sum of the account's entries up to it, which is the same number.
		List<Line> rows = jdbc.sql("""
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
				where e.account_id = :account and (cast(:cursor as bigint) is null or e.seq < :cursor)
				order by e.seq desc limit :limit
				""")
			.param("account", account.get())
			.param("cursor", seqCursor(cursor))
			.param("limit", size + 1)
			.query(Line::map)
			.list();
		if (rows.size() <= size) {
			return new Page<>(List.copyOf(rows), null);
		}
		List<Line> items = List.copyOf(rows.subList(0, size));
		return new Page<>(items, String.valueOf(items.getLast().seq()));
	}

	/**
	 * Opening balance, credits, debits and closing balance of {@code [from, to)}, with the station's
	 * position now. A null {@code from} starts at the first entry; a null {@code to} ends at the last.
	 */
	@Transactional(readOnly = true)
	Period period(ZawedPrincipal principal, UUID stationId, Instant from, Instant to) {
		access.require(principal, stationId);
		if (from != null && to != null && !from.isBefore(to)) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_PERIOD", "The period must start before it ends", "from",
					null);
		}
		Position position = book.position(stationId);
		Optional<UUID> account = account(stationId);
		if (account.isEmpty()) {
			return new Period(stationId, from, to, 0, 0, 0, 0, 0, position);
		}
		// One statement, one snapshot: the four numbers always add up, whatever is posted meanwhile.
		return jdbc.sql("""
				select coalesce(sum(case e.direction when 'CREDIT' then e.amount_piastres else -e.amount_piastres end)
				                  filter (where e.posted_at < cast(:from as timestamptz)), 0)::bigint as opening,
				       coalesce(sum(e.amount_piastres) filter (where e.direction = 'CREDIT'
				                  and (cast(:from as timestamptz) is null or e.posted_at >= :from)), 0)::bigint as credits,
				       coalesce(sum(e.amount_piastres) filter (where e.direction = 'DEBIT'
				                  and (cast(:from as timestamptz) is null or e.posted_at >= :from)), 0)::bigint as debits,
				       count(*) filter (where cast(:from as timestamptz) is null or e.posted_at >= :from) as entries
				from ledger_entries e
				where e.account_id = :account and (cast(:to as timestamptz) is null or e.posted_at < :to)
				""")
			.param("account", account.get())
			.param("from", ts(from))
			.param("to", ts(to))
			.query((rs, n) -> {
				long opening = rs.getLong("opening");
				long credits = rs.getLong("credits");
				long debits = rs.getLong("debits");
				return new Period(stationId, from, to, opening, credits, debits,
						Math.subtractExact(Math.addExact(opening, credits), debits), rs.getInt("entries"), position);
			})
			.single();
	}

	/** The station's payable account, once anything was posted to it. */
	private Optional<UUID> account(UUID stationId) {
		return jdbc.sql("select account_id from station_payables where station_id = :station")
			.param("station", stationId)
			.query(UUID.class)
			.optional();
	}

	/** The statement's cursor is an entry sequence number, as for the wallet statement. */
	private static Long seqCursor(String value) {
		if (value == null || value.isBlank()) {
			return null;
		}
		try {
			return Long.parseLong(value.strip());
		}
		catch (NumberFormatException ex) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_CURSOR", "Invalid cursor", "cursor", null);
		}
	}

}
