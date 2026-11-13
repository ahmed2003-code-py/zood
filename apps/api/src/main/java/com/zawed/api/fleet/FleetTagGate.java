package com.zawed.api.fleet;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.time.Clock;
import java.time.Instant;
import java.util.List;
import java.util.Optional;
import java.util.UUID;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Component;

import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.crypto.LookupHasher;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.rules.FuelingRules.LimitKind;
import com.zawed.api.rules.Periods.Period;

@Component
class FleetTagGate implements TagGate {

	private final JdbcClient jdbc;
	private final LookupHasher lookup;
	private final Clock clock;

	FleetTagGate(JdbcClient jdbc, LookupHasher lookup, Clock clock) {
		this.jdbc = jdbc;
		this.lookup = lookup;
		this.clock = clock;
	}

	@Override
	public Optional<Bound> find(String rawUid) {
		String uid;
		try {
			uid = Tags.normalizeUid(rawUid);
		}
		catch (ApiException ex) {
			return Optional.empty();
		}
		return jdbc.sql("select id, company_id from rfid_tags where uid_lookup = :lookup")
			.param("lookup", lookup.hash(Tags.LOOKUP_NAMESPACE, uid))
			.query((rs, n) -> new Bound(rs.getObject("id", UUID.class), rs.getObject("company_id", UUID.class)))
			.optional();
	}

	@Override
	public TagAtPump lock(UUID tagId) {
		// Vehicle before tag, the order the fleet's own changes take (vehicle FOR UPDATE, then its
		// tags): a tag reported stolen while in use waits instead of deadlocking (B4 review 3). A
		// tag never changes vehicle, so reading its vehicle id first is safe.
		jdbc.sql("select v.id from vehicles v join rfid_tags t on t.vehicle_id = v.id where t.id = :id for share of v")
			.param("id", tagId)
			.query(UUID.class)
			.optional();
		return jdbc.sql("""
				select t.id, t.vehicle_id, t.company_id, t.status as tag_status, t.cooldown_until, v.plate, v.fuel_type,
				       v.status as vehicle_status, v.tier, v.tank_capacity_ml, v.vip_monthly_ceiling_ml
				from rfid_tags t join vehicles v on v.id = t.vehicle_id
				where t.id = :id
				for update of t
				""")
			.param("id", tagId)
			.query((rs, n) -> new TagAtPump(rs.getObject("id", UUID.class), rs.getObject("vehicle_id", UUID.class),
					rs.getObject("company_id", UUID.class), "ACTIVE".equals(rs.getString("tag_status")),
					instant(rs, "cooldown_until"), rs.getString("plate"), FuelType.valueOf(rs.getString("fuel_type")),
					"ACTIVE".equals(rs.getString("vehicle_status")), "VIP".equals(rs.getString("tier")),
					rs.getLong("tank_capacity_ml"), rs.getObject("vip_monthly_ceiling_ml", Long.class)))
			.optional()
			.orElseThrow(() -> ApiException.notFound("Tag"));
	}

	@Override
	public List<LimitRule> activeLimits(UUID vehicleId) {
		return jdbc.sql("select kind, period, max_value from limit_rules where vehicle_id = :vehicle and active")
			.param("vehicle", vehicleId)
			.query((rs, n) -> new LimitRule(LimitKind.valueOf(rs.getString("kind")), Period.valueOf(rs.getString("period")),
					rs.getLong("max_value")))
			.list();
	}

	@Override
	public void startCooldown(UUID tagId, Instant until) {
		jdbc.sql("update rfid_tags set cooldown_until = :until, updated_at = :now where id = :id")
			.param("id", tagId)
			.param("until", ts(until))
			.param("now", ts(clock.instant()))
			.update();
	}

}
