package com.zawed.api.fueling;

import static com.zawed.api.platform.persistence.Sql.ts;

import java.time.Instant;
import java.util.HashMap;
import java.util.Map;
import java.util.UUID;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Component;

@Component
class FuelingUsageReader implements FuelingUsage {

	private final JdbcClient jdbc;

	FuelingUsageReader(JdbcClient jdbc) {
		this.jdbc = jdbc;
	}

	@Override
	public Map<UUID, Usage> usageSince(Instant dayStart, Instant weekStart, Instant monthStart) {
		Instant earliest = weekStart.isBefore(monthStart) ? weekStart : monthStart;
		Map<UUID, Usage> usage = new HashMap<>();
		jdbc.sql("""
				select vehicle_id,
				       coalesce(sum(volume_ml) filter (where evaluated_at >= :day), 0)::bigint as day_ml,
				       coalesce(sum(volume_ml) filter (where evaluated_at >= :week), 0)::bigint as week_ml,
				       coalesce(sum(volume_ml) filter (where evaluated_at >= :month), 0)::bigint as month_ml,
				       coalesce(sum(amount_piastres) filter (where evaluated_at >= :day), 0)::bigint as day_p,
				       coalesce(sum(amount_piastres) filter (where evaluated_at >= :week), 0)::bigint as week_p,
				       coalesce(sum(amount_piastres) filter (where evaluated_at >= :month), 0)::bigint as month_p
				from fuelings
				where status = 'COMPLETED' and evaluated_at >= :earliest
				group by vehicle_id
				""")
			.param("day", ts(dayStart))
			.param("week", ts(weekStart))
			.param("month", ts(monthStart))
			.param("earliest", ts(earliest.isBefore(dayStart) ? earliest : dayStart))
			.query((rs) -> {
				usage.put(rs.getObject("vehicle_id", UUID.class), new Usage(rs.getLong("day_ml"), rs.getLong("week_ml"),
						rs.getLong("month_ml"), rs.getLong("day_p"), rs.getLong("week_p"), rs.getLong("month_p")));
			});
		return usage;
	}

}
