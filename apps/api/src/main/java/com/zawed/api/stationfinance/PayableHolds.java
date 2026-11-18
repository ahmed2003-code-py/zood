package com.zawed.api.stationfinance;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;
import static com.zawed.api.stationfinance.BankAccounts.actorOf;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * Held payables (B5 contract §4): an offline fueling recorded past the device's offline bound, or at
 * a station that was not open, credits the station in full but its amount waits for Operations
 * before any payout. Operations releases it here, with a reason.
 *
 * <p>A fueling row never changes (V7_1), so a release is a row of its own, written once
 * ({@code payable_hold_releases}, append only). The moment it commits, the amount counts as
 * available ({@link PayableBook}). A held fueling that was reversed is no longer held either: its
 * payable was taken back.
 */
@Service
class PayableHolds {

	enum State {

		/** Excluded from what may be paid out. */
		HELD,
		/** Operations released it; it counts as available. */
		RELEASED,
		/** The fueling was reversed; there is nothing left to hold. */
		REVERSED

	}

	/** A fueling whose payable was held at sync, with what became of the hold. */
	record HeldPayable(UUID fuelingId, String reference, String plate, UUID stationId, UUID operatorId,
			long amountPiastres, Instant evaluatedAt, Instant receivedAt, List<String> conflicts, State state,
			UUID releasedBy, Instant releasedAt, String releaseReason) {

		static HeldPayable map(ResultSet rs, int row) throws SQLException {
			return new HeldPayable(rs.getObject("id", UUID.class), rs.getString("reference"), rs.getString("plate"),
					rs.getObject("station_id", UUID.class), rs.getObject("operator_id", UUID.class),
					rs.getLong("amount_piastres"), instant(rs, "evaluated_at"), instant(rs, "received_at"),
					List.of((String[]) rs.getArray("conflicts").getArray()), State.valueOf(rs.getString("state")),
					rs.getObject("released_by", UUID.class), instant(rs, "released_at"), rs.getString("release_reason"));
		}

	}

	/**
	 * Held fuelings with the state of their hold. Only fuelings that posted: a held fueling with no
	 * amount holds nothing.
	 */
	private static final String HELD = """
			select f.id, f.reference, f.plate, f.station_id, f.operator_id, f.amount_piastres, f.evaluated_at,
			       f.received_at, f.conflicts, h.released_by, h.released_at, h.reason as release_reason,
			       case when exists (select 1 from fueling_reversals r where r.fueling_id = f.id and r.status = 'APPROVED')
			              then 'REVERSED'
			            when h.id is not null then 'RELEASED'
			            else 'HELD' end as state
			from fuelings f left join payable_hold_releases h on h.fueling_id = f.id
			where f.payable_held and f.ledger_transaction_id is not null
			""";

	private final JdbcClient jdbc;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;

	PayableHolds(JdbcClient jdbc, AuditLog audit, IdGenerator ids, Clock clock) {
		this.jdbc = jdbc;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
	}

	/** Held fuelings, newest first; {@code state} null lists them all. */
	@Transactional(readOnly = true)
	Page<HeldPayable> list(UUID operatorId, UUID stationId, State state, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<HeldPayable> rows = jdbc.sql("select * from (" + HELD + """
				) held
				where (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:station as uuid) is null or station_id = :station)
				  and (cast(:state as text) is null or state = :state)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("operator", operatorId)
			.param("station", stationId)
			.param("state", state == null ? null : state.name())
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(HeldPayable::map)
			.list();
		return Page.of(rows, size, HeldPayable::fuelingId);
	}

	/** Releases the hold on one fueling's payable. Once: a release is never undone or repeated. */
	@Transactional
	HeldPayable release(ZawedPrincipal actor, UUID fuelingId, String reason) {
		// A fueling that was never held (or does not exist, or posted nothing) has no hold to release.
		HeldPayable held = find(fuelingId).orElseThrow(() -> jdbc.sql("select 1 from fuelings where id = :id")
			.param("id", fuelingId)
			.query(Integer.class)
			.optional()
			.map((exists) -> new ApiException(HttpStatus.CONFLICT, "FUELING_NOT_HELD", "This fueling's payable is not held"))
			.orElseGet(() -> ApiException.notFound("Fueling")));
		if (held.state() == State.RELEASED) {
			throw alreadyReleased();
		}
		if (held.state() == State.REVERSED) {
			throw new ApiException(HttpStatus.CONFLICT, "FUELING_REVERSED",
					"This fueling was reversed; its payable was taken back and is no longer held");
		}
		UUID id = ids.next();
		Instant now = clock.instant();
		try {
			jdbc.sql("""
					insert into payable_hold_releases (id, fueling_id, station_id, operator_id, amount_piastres, reason,
					                                   released_by, released_at)
					values (:id, :fueling, :station, :operator, :amount, :reason, :actor, :now)
					""")
				.param("id", id)
				.param("fueling", fuelingId)
				.param("station", held.stationId())
				.param("operator", held.operatorId())
				.param("amount", held.amountPiastres())
				.param("reason", reason)
				.param("actor", actor.userId())
				.param("now", ts(now))
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw alreadyReleased(); // a concurrent release of the same fueling won the unique index
		}
		audit.record(actorOf(actor), "PAYABLE_HOLD_RELEASED", "FUELING", fuelingId, Scope.operator(held.operatorId()),
				Map.of("stationId", held.stationId().toString(), "reference", held.reference(), "amountPiastres",
						held.amountPiastres(), "reason", reason));
		return find(fuelingId).orElseThrow();
	}

	private java.util.Optional<HeldPayable> find(UUID fuelingId) {
		return jdbc.sql(HELD + " and f.id = :id").param("id", fuelingId).query(HeldPayable::map).optional();
	}

	private static ApiException alreadyReleased() {
		return new ApiException(HttpStatus.CONFLICT, "HOLD_ALREADY_RELEASED", "This held payable was already released");
	}

}
