package com.zawed.api.fleet;

import static com.zawed.api.platform.persistence.Sql.clean;
import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.nullableInt;
import static com.zawed.api.platform.persistence.Sql.nullableLong;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

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
import com.zawed.api.tenancy.CompanyDirectory;

/**
 * Vehicles and their limit rules (PRD §4.3, §4.5, §6.3). Ops registers and edits; the company reads.
 * A vehicle never leaves its company: the company is fixed at creation, and the database refuses a
 * department, tag or limit from another company.
 */
@Service
class Vehicles {

	enum Tier {

		NORMAL, VIP

	}

	enum Status {

		ACTIVE, SUSPENDED, DECOMMISSIONED

	}

	enum LimitKind {

		/** Millilitres per period. */
		VOLUME,
		/** Piastres per period. */
		AMOUNT

	}

	enum LimitPeriod {

		DAY, WEEK, MONTH

	}

	record Vehicle(UUID id, UUID companyId, UUID departmentId, String plate, String make, String model, Integer year,
			FuelType fuelType, long tankCapacityMl, Tier tier, Long vipMonthlyCeilingMl, Status status,
			Instant createdAt) {

		static Vehicle map(ResultSet rs, int row) throws SQLException {
			return new Vehicle(rs.getObject("id", UUID.class), rs.getObject("company_id", UUID.class),
					rs.getObject("department_id", UUID.class), rs.getString("plate"), rs.getString("make"),
					rs.getString("model"), nullableInt(rs, "year"), FuelType.valueOf(rs.getString("fuel_type")),
					rs.getLong("tank_capacity_ml"), Tier.valueOf(rs.getString("tier")),
					nullableLong(rs, "vip_monthly_ceiling_ml"), Status.valueOf(rs.getString("status")),
					instant(rs, "created_at"));
		}

	}

	record Draft(UUID departmentId, String plate, String make, String model, Integer year, FuelType fuelType,
			long tankCapacityMl, Tier tier, Long vipMonthlyCeilingMl) {
	}

	record LimitRule(UUID id, UUID vehicleId, LimitKind kind, LimitPeriod period, long maxValue, Instant createdAt) {

		static LimitRule map(ResultSet rs, int row) throws SQLException {
			return new LimitRule(rs.getObject("id", UUID.class), rs.getObject("vehicle_id", UUID.class),
					LimitKind.valueOf(rs.getString("kind")), LimitPeriod.valueOf(rs.getString("period")),
					rs.getLong("max_value"), instant(rs, "created_at"));
		}

	}

	private final JdbcClient jdbc;
	private final CompanyDirectory companies;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;

	Vehicles(JdbcClient jdbc, CompanyDirectory companies, AuditLog audit, IdGenerator ids, Clock clock) {
		this.jdbc = jdbc;
		this.companies = companies;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
	}

	// ----------------------------------------------------------------------------------- queries

	@Transactional(readOnly = true)
	Page<Vehicle> list(UUID companyId, UUID departmentId, Status status, String query, String cursor, Integer limit) {
		int size = Page.limit(limit);
		String cleaned = clean(query);
		if (cleaned != null && PlateKey.of(cleaned).isEmpty()) {
			return new Page<>(List.of(), null); // nothing a plate could contain: no match, not every vehicle
		}
		List<Vehicle> rows = jdbc.sql("""
				select * from vehicles
				where (cast(:company as uuid) is null or company_id = :company)
				  and (cast(:department as uuid) is null or department_id = :department)
				  and (cast(:status as text) is null or status = :status)
				  and (cast(:plateKey as text) is null or plate_key like :plateKey)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("company", companyId)
			.param("department", departmentId)
			.param("status", status == null ? null : status.name())
			.param("plateKey", cleaned == null ? null : "%" + PlateKey.of(cleaned) + "%")
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Vehicle::map)
			.list();
		return Page.of(rows, size, Vehicle::id);
	}

	@Transactional(readOnly = true)
	Vehicle get(UUID id) {
		return jdbc.sql("select * from vehicles where id = :id")
			.param("id", id)
			.query(Vehicle::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Vehicle"));
	}

	@Transactional(readOnly = true)
	List<LimitRule> limits(UUID vehicleId) {
		get(vehicleId);
		return jdbc.sql("select * from limit_rules where vehicle_id = :vehicle and active order by kind, period")
			.param("vehicle", vehicleId)
			.query(LimitRule::map)
			.list();
	}

	// ----------------------------------------------------------------------------------- changes

	@Transactional
	Vehicle create(ZawedPrincipal actor, UUID companyId, Draft draft) {
		if (companies.find(companyId).isEmpty()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "COMPANY_NOT_FOUND", "No such company", "companyId", null);
		}
		checkDraft(companyId, draft);
		UUID id = ids.next();
		Instant now = clock.instant();
		try {
			jdbc.sql("""
					insert into vehicles (id, company_id, department_id, plate, plate_key, make, model, year, fuel_type,
					                      tank_capacity_ml, tier, vip_monthly_ceiling_ml, status, created_at, created_by,
					                      updated_at, updated_by)
					values (:id, :company, :department, :plate, :plateKey, :make, :model, :year, :fuel, :tank, :tier,
					        :ceiling, 'ACTIVE', :now, :actor, :now, :actor)
					""")
				.params(draftParams(draft))
				.param("id", id)
				.param("company", companyId)
				.param("now", ts(now))
				.param("actor", actor.userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw plateInUse();
		}
		audit.record(actorOf(actor), "VEHICLE_CREATED", "VEHICLE", id, Scope.company(companyId),
				Map.of("plate", clean(draft.plate()), "tier", draft.tier().name()));
		return get(id);
	}

	@Transactional
	Vehicle update(ZawedPrincipal actor, UUID id, Draft draft) {
		Vehicle before = lock(id);
		if (before.status() == Status.DECOMMISSIONED) {
			throw decommissioned();
		}
		checkDraft(before.companyId(), draft);
		try {
			jdbc.sql("""
					update vehicles set department_id = :department, plate = :plate, plate_key = :plateKey, make = :make,
					       model = :model, year = :year, fuel_type = :fuel, tank_capacity_ml = :tank, tier = :tier,
					       vip_monthly_ceiling_ml = :ceiling, updated_at = :now, updated_by = :actor
					where id = :id
					""")
				.params(draftParams(draft))
				.param("id", id)
				.param("now", ts(clock.instant()))
				.param("actor", actor.userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw plateInUse();
		}
		Map<String, Object> change = new HashMap<>();
		if (!before.plate().equals(clean(draft.plate()))) {
			change.put("plate", Map.of("from", before.plate(), "to", clean(draft.plate())));
		}
		if (before.tier() != draft.tier()) {
			change.put("tier", Map.of("from", before.tier().name(), "to", draft.tier().name()));
		}
		if (before.fuelType() != draft.fuelType()) {
			change.put("fuelType", Map.of("from", before.fuelType().name(), "to", draft.fuelType().name()));
		}
		audit.record(actorOf(actor), "VEHICLE_UPDATED", "VEHICLE", id, Scope.company(before.companyId()), change);
		return get(id);
	}

	/**
	 * Suspends, reactivates or decommissions a vehicle. Decommissioning is final: it frees the plate,
	 * deactivates the vehicle's tags and retires its limits in the same transaction.
	 */
	@Transactional
	Vehicle setStatus(ZawedPrincipal actor, UUID id, Status status, String reason) {
		Vehicle before = lock(id);
		if (before.status() == status) {
			return before;
		}
		if (before.status() == Status.DECOMMISSIONED) {
			throw decommissioned();
		}
		Instant now = clock.instant();
		jdbc.sql("update vehicles set status = :status, updated_at = :now, updated_by = :actor where id = :id")
			.param("id", id)
			.param("status", status.name())
			.param("now", ts(now))
			.param("actor", actor.userId())
			.update();
		int tags = 0;
		if (status == Status.DECOMMISSIONED) {
			tags = jdbc.sql("""
					update rfid_tags set status = 'DEACTIVATED', status_reason = 'Vehicle decommissioned',
					       updated_at = :now, updated_by = :actor
					where vehicle_id = :id and status in ('ACTIVE', 'REPLACEMENT_PENDING')
					""").param("id", id).param("now", ts(now)).param("actor", actor.userId()).update();
			jdbc.sql("""
					update limit_rules set active = false, deactivated_at = :now, deactivated_by = :actor
					where vehicle_id = :id and active
					""").param("id", id).param("now", ts(now)).param("actor", actor.userId()).update();
		}
		audit.record(actorOf(actor), "VEHICLE_STATUS_CHANGED", "VEHICLE", id, Scope.company(before.companyId()),
				Map.of("from", before.status().name(), "to", status.name(), "reason", reason, "tagsDeactivated", tags));
		return get(id);
	}

	/**
	 * Sets the one active rule for a kind and period: the previous rule is retired, never edited, so
	 * the history of what applied when stays readable.
	 */
	@Transactional
	LimitRule setLimit(ZawedPrincipal actor, UUID vehicleId, LimitKind kind, LimitPeriod period, long maxValue) {
		Vehicle vehicle = lock(vehicleId);
		if (vehicle.status() == Status.DECOMMISSIONED) {
			throw decommissioned();
		}
		Instant now = clock.instant();
		List<Long> previous = jdbc.sql("""
				update limit_rules set active = false, deactivated_at = :now, deactivated_by = :actor
				where vehicle_id = :vehicle and kind = :kind and period = :period and active
				returning max_value
				""")
			.param("vehicle", vehicleId)
			.param("kind", kind.name())
			.param("period", period.name())
			.param("now", ts(now))
			.param("actor", actor.userId())
			.query(Long.class)
			.list();
		UUID id = ids.next();
		jdbc.sql("""
				insert into limit_rules (id, company_id, vehicle_id, kind, period, max_value, active, created_at, created_by)
				values (:id, :company, :vehicle, :kind, :period, :max, true, :now, :actor)
				""")
			.param("id", id)
			.param("company", vehicle.companyId())
			.param("vehicle", vehicleId)
			.param("kind", kind.name())
			.param("period", period.name())
			.param("max", maxValue)
			.param("now", ts(now))
			.param("actor", actor.userId())
			.update();
		Map<String, Object> change = new HashMap<>();
		change.put("kind", kind.name());
		change.put("period", period.name());
		change.put("to", maxValue);
		if (!previous.isEmpty()) {
			change.put("from", previous.getFirst());
		}
		audit.record(actorOf(actor), "LIMIT_SET", "VEHICLE", vehicleId, Scope.company(vehicle.companyId()), change);
		return jdbc.sql("select * from limit_rules where id = :id").param("id", id).query(LimitRule::map).single();
	}

	@Transactional
	void removeLimit(ZawedPrincipal actor, UUID vehicleId, UUID limitId) {
		Vehicle vehicle = lock(vehicleId);
		List<LimitRule> removed = jdbc.sql("""
				update limit_rules set active = false, deactivated_at = :now, deactivated_by = :actor
				where id = :id and vehicle_id = :vehicle and active
				returning *
				""")
			.param("id", limitId)
			.param("vehicle", vehicleId)
			.param("now", ts(clock.instant()))
			.param("actor", actor.userId())
			.query(LimitRule::map)
			.list();
		if (removed.isEmpty()) {
			throw ApiException.notFound("Limit");
		}
		LimitRule rule = removed.getFirst();
		audit.record(actorOf(actor), "LIMIT_REMOVED", "VEHICLE", vehicleId, Scope.company(vehicle.companyId()),
				Map.of("kind", rule.kind().name(), "period", rule.period().name(), "from", rule.maxValue()));
	}

	// ----------------------------------------------------------------------------------- helpers

	/** Reads the vehicle and holds its row until commit, so changes to one vehicle run one at a time. */
	Vehicle lock(UUID id) {
		return jdbc.sql("select * from vehicles where id = :id for update")
			.param("id", id)
			.query(Vehicle::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Vehicle"));
	}

	private void checkDraft(UUID companyId, Draft draft) {
		if (draft.tier() != Tier.VIP && draft.vipMonthlyCeilingMl() != null) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "VIP_CEILING_ONLY_FOR_VIP",
					"A monthly ceiling applies to VIP vehicles only", "vipMonthlyCeilingMl", null);
		}
		if (PlateKey.of(draft.plate()).isEmpty()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_PLATE", "The plate has no letters or digits", "plate",
					null);
		}
		if (draft.departmentId() != null && !companies.hasActiveDepartment(companyId, draft.departmentId())) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "DEPARTMENT_NOT_FOUND", "No such department in this company",
					"departmentId", null);
		}
	}

	private static Map<String, Object> draftParams(Draft draft) {
		Map<String, Object> params = new HashMap<>();
		params.put("department", draft.departmentId());
		params.put("plate", clean(draft.plate()));
		params.put("plateKey", PlateKey.of(draft.plate()));
		params.put("make", clean(draft.make()));
		params.put("model", clean(draft.model()));
		params.put("year", draft.year());
		params.put("fuel", draft.fuelType().name());
		params.put("tank", draft.tankCapacityMl());
		params.put("tier", draft.tier().name());
		params.put("ceiling", draft.vipMonthlyCeilingMl());
		return params;
	}

	private static ApiException plateInUse() {
		return new ApiException(HttpStatus.CONFLICT, "PLATE_IN_USE", "Another vehicle has this plate", "plate", null);
	}

	private static ApiException decommissioned() {
		return new ApiException(HttpStatus.CONFLICT, "VEHICLE_DECOMMISSIONED", "The vehicle is decommissioned");
	}

	static AuditLog.Actor actorOf(ZawedPrincipal actor) {
		return new AuditLog.Actor(actor.userId(), actor.role());
	}

}
