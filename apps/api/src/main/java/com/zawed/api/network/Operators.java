package com.zawed.api.network;

import static com.zawed.api.platform.persistence.Sql.clean;
import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.math.BigDecimal;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.DateTimeException;
import java.time.Instant;
import java.time.ZoneId;
import java.util.Arrays;
import java.util.EnumSet;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.UUID;
import java.util.stream.Collectors;

import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * Station operators and their stations (PRD §6.3 Onboard Station). Ops onboards and edits; the
 * operator's station manager reads. Every change is audited under the operator's scope.
 */
@Service
class Operators implements OperatorDirectory {

	enum OperatorStatus {

		ACTIVE, SUSPENDED

	}

	enum StationStatus {

		ACTIVE, SUSPENDED, CLOSED

	}

	enum Connectivity {

		NORMAL, LOW

	}

	record Operator(UUID id, String legalName, String nameAr, String nameEn, String taxId,
			String commercialRegistration, String contactName, String contactPhone, String contactEmail,
			OperatorStatus status, Instant createdAt) {

		static Operator map(ResultSet rs, int row) throws SQLException {
			return new Operator(rs.getObject("id", UUID.class), rs.getString("legal_name"), rs.getString("name_ar"),
					rs.getString("name_en"), rs.getString("tax_id"), rs.getString("commercial_registration"),
					rs.getString("contact_name"), rs.getString("contact_phone"), rs.getString("contact_email"),
					OperatorStatus.valueOf(rs.getString("status")), instant(rs, "created_at"));
		}

	}

	record OperatorDraft(String legalName, String nameAr, String nameEn, String taxId, String commercialRegistration,
			String contactName, String contactPhone, String contactEmail) {
	}

	record Station(UUID id, UUID operatorId, String nameAr, String nameEn, String address, BigDecimal latitude,
			BigDecimal longitude, String timeZone, Connectivity connectivity, Set<FuelType> fuelTypes,
			StationStatus status, Instant createdAt) {

		static Station map(ResultSet rs, int row) throws SQLException {
			String[] fuels = (String[]) rs.getArray("fuel_types").getArray();
			return new Station(rs.getObject("id", UUID.class), rs.getObject("operator_id", UUID.class),
					rs.getString("name_ar"), rs.getString("name_en"), rs.getString("address"),
					rs.getBigDecimal("latitude"), rs.getBigDecimal("longitude"), rs.getString("time_zone"),
					Connectivity.valueOf(rs.getString("connectivity")),
					Arrays.stream(fuels)
						.map(FuelType::valueOf)
						.collect(Collectors.toCollection(() -> EnumSet.noneOf(FuelType.class))),
					StationStatus.valueOf(rs.getString("status")), instant(rs, "created_at"));
		}

	}

	record StationDraft(String nameAr, String nameEn, String address, BigDecimal latitude, BigDecimal longitude,
			String timeZone, Connectivity connectivity, Set<FuelType> fuelTypes) {
	}

	static final String DEFAULT_TIME_ZONE = "Africa/Cairo";

	private final JdbcClient jdbc;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;

	Operators(JdbcClient jdbc, AuditLog audit, IdGenerator ids, Clock clock) {
		this.jdbc = jdbc;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
	}

	// --------------------------------------------------------------------------------- directory

	@Override
	@Transactional(readOnly = true)
	public Optional<OperatorRef> find(UUID operatorId) {
		return jdbc.sql("select id, name_ar, name_en, status from station_operators where id = :id")
			.param("id", operatorId)
			.query((rs, n) -> new OperatorRef(rs.getObject("id", UUID.class), rs.getString("name_ar"),
					rs.getString("name_en"), "ACTIVE".equals(rs.getString("status"))))
			.optional();
	}

	@Override
	@Transactional(readOnly = true)
	public boolean stationExists(UUID stationId) {
		return Boolean.TRUE.equals(jdbc.sql("select exists (select 1 from stations where id = :id)")
			.param("id", stationId)
			.query(Boolean.class)
			.single());
	}

	// --------------------------------------------------------------------------------- operators

	@Transactional(readOnly = true)
	Page<Operator> list(OperatorStatus status, String query, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Operator> rows = jdbc.sql("""
				select * from station_operators
				where (cast(:status as text) is null or status = :status)
				  and (cast(:q as text) is null or lower(legal_name) like :q or lower(name_ar) like :q
				       or lower(name_en) like :q or tax_id = :raw)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("status", status == null ? null : status.name())
			.param("q", like(query))
			.param("raw", clean(query))
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Operator::map)
			.list();
		return Page.of(rows, size, Operator::id);
	}

	@Transactional(readOnly = true)
	Operator get(UUID id) {
		return jdbc.sql("select * from station_operators where id = :id")
			.param("id", id)
			.query(Operator::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Operator"));
	}

	@Transactional
	Operator create(ZawedPrincipal actor, OperatorDraft draft) {
		UUID id = ids.next();
		Instant now = clock.instant();
		try {
			jdbc.sql("""
					insert into station_operators (id, legal_name, name_ar, name_en, tax_id, commercial_registration,
					                               contact_name, contact_phone, contact_email, status, created_at,
					                               created_by, updated_at, updated_by)
					values (:id, :legal, :ar, :en, :tax, :cr, :contact, :phone, :email, 'ACTIVE', :now, :actor, :now,
					        :actor)
					""")
				.params(operatorParams(draft))
				.param("id", id)
				.param("now", ts(now))
				.param("actor", actor.userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw taxIdInUse();
		}
		audit.record(actorOf(actor), "OPERATOR_CREATED", "OPERATOR", id, Scope.operator(id), Map.of());
		return get(id);
	}

	@Transactional
	Operator update(ZawedPrincipal actor, UUID id, OperatorDraft draft) {
		Operator before = lockOperator(id);
		try {
			jdbc.sql("""
					update station_operators set legal_name = :legal, name_ar = :ar, name_en = :en, tax_id = :tax,
					       commercial_registration = :cr, contact_name = :contact, contact_phone = :phone,
					       contact_email = :email, updated_at = :now, updated_by = :actor
					where id = :id
					""")
				.params(operatorParams(draft))
				.param("id", id)
				.param("now", ts(clock.instant()))
				.param("actor", actor.userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw taxIdInUse();
		}
		audit.record(actorOf(actor), "OPERATOR_UPDATED", "OPERATOR", id, Scope.operator(id),
				Map.of("legalNameBefore", before.legalName()));
		return get(id);
	}

	/** Suspending an operator stops its devices from fueling (enforced by the fueling engine). */
	@Transactional
	Operator setStatus(ZawedPrincipal actor, UUID id, OperatorStatus status, String reason) {
		Operator before = lockOperator(id);
		if (before.status() == status) {
			return before;
		}
		jdbc.sql("update station_operators set status = :status, updated_at = :now, updated_by = :actor where id = :id")
			.param("id", id)
			.param("status", status.name())
			.param("now", ts(clock.instant()))
			.param("actor", actor.userId())
			.update();
		audit.record(actorOf(actor), "OPERATOR_STATUS_CHANGED", "OPERATOR", id, Scope.operator(id),
				Map.of("from", before.status().name(), "to", status.name(), "reason", reason));
		return get(id);
	}

	// ---------------------------------------------------------------------------------- stations

	@Transactional(readOnly = true)
	Page<Station> stations(UUID operatorId, StationStatus status, String query, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Station> rows = jdbc.sql("""
				select * from stations
				where (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:status as text) is null or status = :status)
				  and (cast(:q as text) is null or lower(name_ar) like :q or lower(name_en) like :q
				       or lower(address) like :q)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("operator", operatorId)
			.param("status", status == null ? null : status.name())
			.param("q", like(query))
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Station::map)
			.list();
		return Page.of(rows, size, Station::id);
	}

	@Transactional(readOnly = true)
	Station station(UUID id) {
		return jdbc.sql("select * from stations where id = :id")
			.param("id", id)
			.query(Station::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Station"));
	}

	@Transactional
	Station createStation(ZawedPrincipal actor, UUID operatorId, StationDraft draft) {
		if (find(operatorId).isEmpty()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "OPERATOR_NOT_FOUND", "No such operator", "operatorId", null);
		}
		UUID id = ids.next();
		Instant now = clock.instant();
		jdbc.sql("""
				insert into stations (id, operator_id, name_ar, name_en, address, latitude, longitude, time_zone,
				                      connectivity, fuel_types, status, created_at, created_by, updated_at, updated_by)
				values (:id, :operator, :ar, :en, :address, :lat, :lon, :zone, :connectivity, cast(:fuels as text[]),
				        'ACTIVE', :now, :actor, :now, :actor)
				""")
			.params(stationParams(draft))
			.param("id", id)
			.param("operator", operatorId)
			.param("now", ts(now))
			.param("actor", actor.userId())
			.update();
		audit.record(actorOf(actor), "STATION_CREATED", "STATION", id, Scope.operator(operatorId),
				Map.of("nameEn", clean(draft.nameEn())));
		return station(id);
	}

	@Transactional
	Station updateStation(ZawedPrincipal actor, UUID id, StationDraft draft) {
		Station before = lockStation(id);
		jdbc.sql("""
				update stations set name_ar = :ar, name_en = :en, address = :address, latitude = :lat, longitude = :lon,
				       time_zone = :zone, connectivity = :connectivity, fuel_types = cast(:fuels as text[]),
				       updated_at = :now, updated_by = :actor
				where id = :id
				""")
			.params(stationParams(draft))
			.param("id", id)
			.param("now", ts(clock.instant()))
			.param("actor", actor.userId())
			.update();
		Map<String, Object> change = new HashMap<>();
		change.put("nameEnBefore", before.nameEn());
		if (before.connectivity() != draft.connectivity()) {
			change.put("connectivity", Map.of("from", before.connectivity().name(), "to", draft.connectivity().name()));
		}
		audit.record(actorOf(actor), "STATION_UPDATED", "STATION", id, Scope.operator(before.operatorId()), change);
		return station(id);
	}

	@Transactional
	Station setStationStatus(ZawedPrincipal actor, UUID id, StationStatus status, String reason) {
		Station before = lockStation(id);
		if (before.status() == status) {
			return before;
		}
		if (before.status() == StationStatus.CLOSED) {
			throw new ApiException(HttpStatus.CONFLICT, "STATION_CLOSED", "A closed station cannot be reopened");
		}
		jdbc.sql("update stations set status = :status, updated_at = :now, updated_by = :actor where id = :id")
			.param("id", id)
			.param("status", status.name())
			.param("now", ts(clock.instant()))
			.param("actor", actor.userId())
			.update();
		audit.record(actorOf(actor), "STATION_STATUS_CHANGED", "STATION", id, Scope.operator(before.operatorId()),
				Map.of("from", before.status().name(), "to", status.name(), "reason", reason));
		return station(id);
	}

	/** Reads the operator and holds its row until commit, so two changes never both read the same "before". */
	private Operator lockOperator(UUID id) {
		return jdbc.sql("select * from station_operators where id = :id for update")
			.param("id", id)
			.query(Operator::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Operator"));
	}

	/** Reads the station and holds its row until commit. */
	Station lockStation(UUID id) {
		return jdbc.sql("select * from stations where id = :id for update")
			.param("id", id)
			.query(Station::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Station"));
	}

	// ----------------------------------------------------------------------------------- helpers

	private static Map<String, Object> operatorParams(OperatorDraft draft) {
		Map<String, Object> params = new HashMap<>();
		params.put("legal", clean(draft.legalName()));
		params.put("ar", clean(draft.nameAr()));
		params.put("en", clean(draft.nameEn()));
		params.put("tax", clean(draft.taxId()));
		params.put("cr", clean(draft.commercialRegistration()));
		params.put("contact", clean(draft.contactName()));
		params.put("phone", clean(draft.contactPhone()));
		String email = clean(draft.contactEmail());
		params.put("email", email == null ? null : email.toLowerCase());
		return params;
	}

	private static Map<String, Object> stationParams(StationDraft draft) {
		String zone = clean(draft.timeZone()) == null ? DEFAULT_TIME_ZONE : clean(draft.timeZone());
		try {
			zone = ZoneId.of(zone).getId();
		}
		catch (DateTimeException ex) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_TIME_ZONE", "Unknown time zone", "timeZone", null);
		}
		if ((draft.latitude() == null) != (draft.longitude() == null)) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "INCOMPLETE_LOCATION", "Give both latitude and longitude",
					"latitude", null);
		}
		Map<String, Object> params = new HashMap<>();
		params.put("ar", clean(draft.nameAr()));
		params.put("en", clean(draft.nameEn()));
		params.put("address", clean(draft.address()));
		params.put("lat", draft.latitude());
		params.put("lon", draft.longitude());
		params.put("zone", zone);
		params.put("connectivity", draft.connectivity().name());
		params.put("fuels", draft.fuelTypes()
			.stream()
			.sorted()
			.map(FuelType::name)
			.collect(Collectors.joining(",", "{", "}")));
		return params;
	}

	static String like(String query) {
		String cleaned = clean(query);
		if (cleaned == null) {
			return null;
		}
		return "%" + cleaned.toLowerCase().replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%";
	}

	private static ApiException taxIdInUse() {
		return new ApiException(HttpStatus.CONFLICT, "TAX_ID_IN_USE", "Another operator has this tax id", "taxId", null);
	}

	static AuditLog.Actor actorOf(ZawedPrincipal actor) {
		return new AuditLog.Actor(actor.userId(), actor.role());
	}

}
