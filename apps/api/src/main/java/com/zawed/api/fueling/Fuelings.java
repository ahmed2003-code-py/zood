package com.zawed.api.fueling;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Timestamp;
import java.time.Instant;
import java.util.Arrays;
import java.util.List;
import java.util.Optional;
import java.util.UUID;
import java.util.stream.Collectors;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Repository;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;
import com.zawed.api.rules.FuelingRules.DeclineReason;

/**
 * Fueling records (plan §3.4). A row is written once by the engine and never updated; the runtime
 * role has no UPDATE or DELETE on the table. Reads run in the caller's scope: row-level security
 * shows a company its own fuelings and an operator the fuelings at its stations.
 *
 * <p>An online fueling is decided: completed, or declined with a reason. An offline one (B5) has
 * already happened at the pump, so it is always completed and carries the checks that failed as
 * {@link Conflict}s instead.
 */
@Repository
class Fuelings {

	enum Status {

		COMPLETED, DECLINED

	}

	enum Origin {

		ONLINE, OFFLINE

	}

	/**
	 * What an offline fueling's checks found (B5 contract §4): the reasons an online fueling is
	 * declined for, which offline are recorded and alerted instead, and a capture that reached the
	 * server long after it was made.
	 */
	enum Conflict {

		COOLDOWN, TAG_INACTIVE, VEHICLE_INACTIVE, COMPANY_FROZEN, FUEL_TYPE_MISMATCH, FUEL_NOT_AVAILABLE, LIMIT_EXCEEDED,
		INSUFFICIENT_BALANCE,
		/** Captured more than seven days before the server received it. */
		STALE_CAPTURE,
		/** The agent belongs to the station but no session of theirs on this device covers the time. */
		AGENT_UNVERIFIED,
		/** The device dated it ahead of server time; judged at the time it arrived instead. */
		DEVICE_CLOCK_SKEW,
		/** The same capture was declined online; its answer was lost and the terminal let the fuel go. */
		DECLINED_ONLINE,
		/** The station (or its operator) was not open for fuelings. */
		STATION_NOT_ACTIVE,
		/** The price the terminal showed differs from the price in force; the server's is charged. */
		PRICE_MISMATCH,
		/** The device went past what it may spend offline for this company; the payable is held. */
		OVER_OFFLINE_ALLOWANCE;

		static Conflict of(DeclineReason reason) {
			return valueOf(reason.name());
		}

	}

	/**
	 * @param conflicts empty for an online fueling; for an offline one, every check that failed, in
	 * the order of the checks
	 */
	record Fueling(UUID id, UUID clientTxnId, String reference, Status status, DeclineReason declineReason,
			List<Conflict> conflicts, Origin origin, UUID companyId, UUID vehicleId, UUID tagId, UUID operatorId, UUID stationId, UUID deviceId,
			UUID agentId, FuelType fuelType, long volumeMl, long unitPricePiastres, long amountPiastres,
			boolean billable, Instant evaluatedAt, Instant deviceAt, Instant receivedAt, String receiptLocale,
			String plate, String companyNameAr, String companyNameEn, String stationNameAr, String stationNameEn,
			String agentWorkerId, UUID ledgerTransactionId, boolean payableHeld) {

		Fueling {
			conflicts = List.copyOf(conflicts);
		}

		static Fueling map(ResultSet rs, int row) throws SQLException {
			String reason = rs.getString("decline_reason");
			List<Conflict> conflicts = Arrays.stream((String[]) rs.getArray("conflicts").getArray())
				.map(Conflict::valueOf)
				.toList();
			return new Fueling(rs.getObject("id", UUID.class), rs.getObject("client_txn_id", UUID.class),
					rs.getString("reference"), Status.valueOf(rs.getString("status")),
					reason == null ? null : DeclineReason.valueOf(reason), conflicts, Origin.valueOf(rs.getString("origin")),
					rs.getObject("company_id", UUID.class), rs.getObject("vehicle_id", UUID.class),
					rs.getObject("tag_id", UUID.class), rs.getObject("operator_id", UUID.class),
					rs.getObject("station_id", UUID.class), rs.getObject("device_id", UUID.class),
					rs.getObject("agent_id", UUID.class), FuelType.valueOf(rs.getString("fuel_type")),
					rs.getLong("volume_ml"), rs.getLong("unit_price_piastres"), rs.getLong("amount_piastres"),
					rs.getBoolean("billable"), instant(rs, "evaluated_at"), instant(rs, "device_at"),
					instant(rs, "received_at"), rs.getString("receipt_locale"), rs.getString("plate"),
					rs.getString("company_name_ar"), rs.getString("company_name_en"), rs.getString("station_name_ar"),
					rs.getString("station_name_en"), rs.getString("agent_worker_id"),
					rs.getObject("ledger_transaction_id", UUID.class), rs.getBoolean("payable_held"));
		}

		boolean completed() {
			return status == Status.COMPLETED;
		}

	}

	/** List filters. A null field does not filter. */
	record Filter(UUID companyId, UUID operatorId, UUID stationId, UUID vehicleId, Status status, Instant from,
			Instant to) {
	}

	private final JdbcClient jdbc;

	Fuelings(JdbcClient jdbc) {
		this.jdbc = jdbc;
	}

	/** Short, sequential, printed on the receipt: {@code F-10001}. */
	String nextReference() {
		return "F-" + jdbc.sql("select nextval('fueling_reference_seq')").query(Long.class).single();
	}

	Optional<Fueling> byClientTxnId(UUID clientTxnId) {
		return jdbc.sql("select * from fuelings where client_txn_id = :id")
			.param("id", clientTxnId)
			.query(Fueling::map)
			.optional();
	}

	Optional<Fueling> find(UUID id) {
		return jdbc.sql("select * from fuelings where id = :id").param("id", id).query(Fueling::map).optional();
	}

	/**
	 * What the vehicle used in completed fuelings evaluated in {@code [from, to)}, in millilitres or
	 * piastres. Runs under the engine's wallet lock, so no fueling of the same company can land in
	 * between. The period has an end as well as a start because an offline capture is counted in the
	 * period of its own time (B5), which may be over: later fuelings belong to another period.
	 */
	long volumeIn(UUID vehicleId, Instant from, Instant to) {
		return usage("volume_ml", vehicleId, from, to);
	}

	long amountIn(UUID vehicleId, Instant from, Instant to) {
		return usage("amount_piastres", vehicleId, from, to);
	}

	private long usage(String column, UUID vehicleId, Instant from, Instant to) {
		return jdbc.sql("select coalesce(sum(" + column + "), 0)::bigint from fuelings "
				+ "where vehicle_id = :vehicle and status = 'COMPLETED' and evaluated_at >= :from and evaluated_at < :to")
			.param("vehicle", vehicleId)
			.param("from", ts(from))
			.param("to", ts(to))
			.query(Long.class)
			.single();
	}

	/**
	 * The latest completed fueling of the vehicle evaluated strictly between two instants, if any.
	 * The engine asks it whether an offline capture falls within a cooldown of another fueling.
	 */
	Optional<Instant> latestCompletedBetween(UUID vehicleId, Instant after, Instant before) {
		// max() over no rows is one row holding null, which single() refuses: read it as a list.
		List<Timestamp> latest = jdbc.sql("""
				select max(evaluated_at) from fuelings
				where vehicle_id = :vehicle and status = 'COMPLETED' and evaluated_at > :after and evaluated_at < :before
				""")
			.param("vehicle", vehicleId)
			.param("after", ts(after))
			.param("before", ts(before))
			.query((rs, n) -> rs.getTimestamp(1))
			.list();
		return latest.isEmpty() ? Optional.empty() : Optional.ofNullable(latest.getFirst()).map(Timestamp::toInstant);
	}

	/**
	 * What one device recorded offline for one company with a device time after {@code since}: the
	 * exposure the offline bound limits (B5 review 2).
	 */
	long offlineAmountSince(UUID deviceId, UUID companyId, Instant since) {
		return jdbc.sql("""
				select coalesce(sum(amount_piastres), 0)::bigint from fuelings
				where device_id = :device and company_id = :company and origin = 'OFFLINE' and evaluated_at > :since
				""")
			.param("device", deviceId)
			.param("company", companyId)
			.param("since", ts(since))
			.query(Long.class)
			.single();
	}

	void insert(Fueling f) {
		jdbc.sql("""
				insert into fuelings (id, client_txn_id, reference, status, decline_reason, conflicts, origin, company_id, vehicle_id,
				                      tag_id, operator_id, station_id, device_id, agent_id, fuel_type, volume_ml,
				                      unit_price_piastres, amount_piastres, billable, evaluated_at, device_at, received_at,
				                      receipt_locale, plate, company_name_ar, company_name_en, station_name_ar,
				                      station_name_en, agent_worker_id, ledger_transaction_id, payable_held)
				values (:id, :clientTxnId, :reference, :status, :declineReason, cast(:conflicts as text[]), :origin, :company, :vehicle, :tag, :operator,
				        :station, :device, :agent, :fuel, :volume, :unitPrice, :amount, :billable, :evaluatedAt, :deviceAt,
				        :receivedAt, :locale, :plate, :companyAr, :companyEn, :stationAr, :stationEn, :worker, :ledgerTxn, :held)
				""")
			.param("id", f.id())
			.param("clientTxnId", f.clientTxnId())
			.param("reference", f.reference())
			.param("status", f.status().name())
			.param("declineReason", f.declineReason() == null ? null : f.declineReason().name())
			// A PostgreSQL array literal: the names are enum constants, so nothing needs quoting.
			.param("conflicts", f.conflicts().stream().map(Conflict::name).collect(Collectors.joining(",", "{", "}")))
			.param("origin", f.origin().name())
			.param("company", f.companyId())
			.param("vehicle", f.vehicleId())
			.param("tag", f.tagId())
			.param("operator", f.operatorId())
			.param("station", f.stationId())
			.param("device", f.deviceId())
			.param("agent", f.agentId())
			.param("fuel", f.fuelType().name())
			.param("volume", f.volumeMl())
			.param("unitPrice", f.unitPricePiastres())
			.param("amount", f.amountPiastres())
			.param("billable", f.billable())
			.param("evaluatedAt", ts(f.evaluatedAt()))
			.param("deviceAt", ts(f.deviceAt()))
			.param("receivedAt", ts(f.receivedAt()))
			.param("locale", f.receiptLocale())
			.param("plate", f.plate())
			.param("companyAr", f.companyNameAr())
			.param("companyEn", f.companyNameEn())
			.param("stationAr", f.stationNameAr())
			.param("stationEn", f.stationNameEn())
			.param("worker", f.agentWorkerId())
			.param("ledgerTxn", f.ledgerTransactionId())
			.param("held", f.payableHeld())
			.update();
	}

	// ------------------------------------------------------------------------------------- reads

	@Transactional(readOnly = true)
	Page<Fueling> list(Filter filter, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Fueling> rows = jdbc.sql("""
				select * from fuelings
				where (cast(:company as uuid) is null or company_id = :company)
				  and (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:station as uuid) is null or station_id = :station)
				  and (cast(:vehicle as uuid) is null or vehicle_id = :vehicle)
				  and (cast(:status as text) is null or status = :status)
				  and (cast(:from as timestamptz) is null or evaluated_at >= :from)
				  and (cast(:to as timestamptz) is null or evaluated_at < :to)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("company", filter.companyId())
			.param("operator", filter.operatorId())
			.param("station", filter.stationId())
			.param("vehicle", filter.vehicleId())
			.param("status", filter.status() == null ? null : filter.status().name())
			.param("from", ts(filter.from()))
			.param("to", ts(filter.to()))
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Fueling::map)
			.list();
		return Page.of(rows, size, Fueling::id);
	}

	@Transactional(readOnly = true)
	Fueling get(UUID id) {
		return find(id).orElseThrow(() -> ApiException.notFound("Fueling"));
	}

}
