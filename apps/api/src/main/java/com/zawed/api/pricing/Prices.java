package com.zawed.api.pricing;

import static com.zawed.api.platform.persistence.Sql.clean;
import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;

import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.network.OperatorDirectory;
import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * Fuel prices (roadmap delta M1). Prices are insert-only and effective-dated: a change is a new row
 * scheduled for a time, so the price that applied to any past fueling can always be recomputed.
 * Devices receive scheduled prices ahead and switch at the effective time, offline too.
 */
@Service
class Prices implements PriceBook {

	record Entry(UUID id, FuelType fuelType, UUID stationId, long pricePiastres, Instant effectiveFrom, String note,
			Instant createdAt) {

		static Entry map(ResultSet rs, int row) throws SQLException {
			return new Entry(rs.getObject("id", UUID.class), FuelType.valueOf(rs.getString("fuel_type")),
					rs.getObject("station_id", UUID.class), rs.getLong("price_piastres"), instant(rs, "effective_from"),
					rs.getString("note"), instant(rs, "created_at"));
		}

	}

	/** A few seconds of clock difference between Ops' browser and the server is not backdating. */
	static final Duration BACKDATE_TOLERANCE = Duration.ofMinutes(1);
	static final Duration MAX_LEAD = Duration.ofDays(366);

	private final JdbcClient jdbc;
	private final OperatorDirectory network;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;

	Prices(JdbcClient jdbc, OperatorDirectory network, AuditLog audit, IdGenerator ids, Clock clock) {
		this.jdbc = jdbc;
		this.network = network;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
	}

	@Override
	@Transactional(readOnly = true)
	public Optional<Price> priceAt(UUID stationId, FuelType fuelType, Instant at) {
		return jdbc.sql("""
				select * from fuel_prices
				where fuel_type = :fuel and effective_from <= :at
				  and (station_id is null or station_id = :station)
				order by effective_from desc, station_id nulls last
				limit 1
				""")
			.param("fuel", fuelType.name())
			.param("at", ts(at))
			.param("station", stationId)
			.query(Entry::map)
			.optional()
			.map((e) -> new Price(e.fuelType(), e.stationId(), e.pricePiastres(), e.effectiveFrom()));
	}

	/** The price in force now for every fuel, at one station or nationally. */
	@Transactional(readOnly = true)
	List<Price> current(UUID stationId) {
		if (stationId != null && !network.stationExists(stationId)) {
			throw ApiException.notFound("Station");
		}
		Instant now = clock.instant();
		List<Price> prices = new ArrayList<>();
		for (FuelType fuel : FuelType.values()) {
			Optional<Price> price = stationId == null ? national(fuel, now) : priceAt(stationId, fuel, now);
			price.ifPresent(prices::add);
		}
		return prices;
	}

	/** Every price, past and scheduled, newest effective time first. */
	@Transactional(readOnly = true)
	Page<Entry> history(FuelType fuelType, UUID stationId, boolean nationalOnly, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Entry> rows = jdbc.sql("""
				select * from fuel_prices
				where (cast(:fuel as text) is null or fuel_type = :fuel)
				  and (cast(:station as uuid) is null or station_id = :station)
				  and (not :nationalOnly or station_id is null)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("fuel", fuelType == null ? null : fuelType.name())
			.param("station", stationId)
			.param("nationalOnly", nationalOnly)
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Entry::map)
			.list();
		return Page.of(rows, size, Entry::id);
	}

	@Transactional
	Entry schedule(ZawedPrincipal actor, FuelType fuelType, UUID stationId, long pricePiastres, Instant effectiveFrom,
			String note) {
		Instant now = clock.instant();
		Instant from = effectiveFrom == null ? now : effectiveFrom;
		if (from.isBefore(now.minus(BACKDATE_TOLERANCE))) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "PRICE_BACKDATED",
					"A price cannot take effect in the past: past fuelings keep the price they had", "effectiveFrom", null);
		}
		// Within the tolerance the request is accepted, but the price never takes effect before now:
		// a fueling already priced in the last minute must keep its price.
		if (from.isBefore(now)) {
			from = now;
		}
		if (from.isAfter(now.plus(MAX_LEAD))) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "PRICE_TOO_FAR_AHEAD", "Schedule at most a year ahead",
					"effectiveFrom", null);
		}
		if (stationId != null && !network.stationExists(stationId)) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "STATION_NOT_FOUND", "No such station", "stationId", null);
		}
		UUID id = ids.next();
		try {
			jdbc.sql("""
					insert into fuel_prices (id, fuel_type, station_id, price_piastres, effective_from, note, created_at,
					                         created_by)
					values (:id, :fuel, :station, :price, :from, :note, :now, :actor)
					""")
				.param("id", id)
				.param("fuel", fuelType.name())
				.param("station", stationId)
				.param("price", pricePiastres)
				.param("from", ts(from))
				.param("note", clean(note))
				.param("now", ts(now))
				.param("actor", actor.userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw new ApiException(HttpStatus.CONFLICT, "PRICE_EXISTS",
					"A price for this fuel and place already takes effect at that time", "effectiveFrom", null);
		}
		Map<String, Object> details = new HashMap<>();
		details.put("fuelType", fuelType.name());
		details.put("pricePiastres", pricePiastres);
		details.put("effectiveFrom", from.toString());
		if (stationId != null) {
			details.put("stationId", stationId.toString());
		}
		audit.record(new AuditLog.Actor(actor.userId(), actor.role()), "PRICE_SCHEDULED", "FUEL_PRICE", id,
				Scope.PLATFORM, details);
		return jdbc.sql("select * from fuel_prices where id = :id").param("id", id).query(Entry::map).single();
	}

	private Optional<Price> national(FuelType fuel, Instant at) {
		return jdbc.sql("""
				select * from fuel_prices
				where fuel_type = :fuel and station_id is null and effective_from <= :at
				order by effective_from desc limit 1
				""")
			.param("fuel", fuel.name())
			.param("at", ts(at))
			.query(Entry::map)
			.optional()
			.map((e) -> new Price(e.fuelType(), null, e.pricePiastres(), e.effectiveFrom()));
	}

}
