package com.zawed.api.fueling;

import static com.zawed.api.platform.persistence.Sql.ts;

import java.time.Duration;
import java.time.Instant;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;

import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.platform.web.ApiException;

/**
 * The activity calendar (plan §7 B6): for each Cairo day of a range, what happened to the entity in
 * view: fuelings completed and declined, litres, amount, alerts. Read in the caller's scope, so a
 * company's calendar counts only its own fuelings and an operator's only those at its stations.
 */
@Service
class Activity {

	static final int MAX_DAYS = 366;
	private static final ZoneId CAIRO = ZoneId.of("Africa/Cairo");

	record ActivityDay(@Schema(requiredMode = RequiredMode.REQUIRED) LocalDate day,
			@Schema(requiredMode = RequiredMode.REQUIRED) int completed,
			@Schema(requiredMode = RequiredMode.REQUIRED) int declined,
			@Schema(requiredMode = RequiredMode.REQUIRED) int offline,
			@Schema(requiredMode = RequiredMode.REQUIRED) int withConflicts,
			@Schema(requiredMode = RequiredMode.REQUIRED) long volumeMl,
			@Schema(requiredMode = RequiredMode.REQUIRED) long amountPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) int alerts) {
	}

	/** What the calendar is about. Null fields do not filter. */
	record Subject(UUID companyId, UUID operatorId, UUID stationId, UUID vehicleId, UUID deviceId, UUID agentId) {
	}

	private final JdbcClient jdbc;

	Activity(JdbcClient jdbc) {
		this.jdbc = jdbc;
	}

	/** Every day from {@code from} to {@code to}, inclusive, quiet days included as zeros. */
	@Transactional(readOnly = true)
	List<ActivityDay> days(LocalDate from, LocalDate to, Subject subject) {
		if (from == null || to == null || to.isBefore(from)) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "VALIDATION_FAILED", "from and to are required, to not before from",
					"from", null);
		}
		if (Duration.between(from.atStartOfDay(), to.atStartOfDay()).toDays() >= MAX_DAYS) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "EXPORT_RANGE_TOO_LONG", "At most a year at a time", "to",
					Map.of("maxDays", MAX_DAYS));
		}
		Instant start = from.atStartOfDay(CAIRO).toInstant();
		Instant end = to.plusDays(1).atStartOfDay(CAIRO).toInstant();

		Map<LocalDate, long[]> byDay = new LinkedHashMap<>();
		for (LocalDate day = from; !day.isAfter(to); day = day.plusDays(1)) {
			byDay.put(day, new long[7]);
		}
		jdbc.sql("""
				select (evaluated_at at time zone 'Africa/Cairo')::date as day,
				       count(*) filter (where status = 'COMPLETED') as completed,
				       count(*) filter (where status = 'DECLINED') as declined,
				       count(*) filter (where origin = 'OFFLINE') as offline,
				       count(*) filter (where conflicts <> '{}') as conflicted,
				       coalesce(sum(volume_ml) filter (where status = 'COMPLETED'), 0)::bigint as volume,
				       coalesce(sum(amount_piastres) filter (where status = 'COMPLETED'), 0)::bigint as amount
				from fuelings
				where evaluated_at >= :start and evaluated_at < :end
				  and (cast(:company as uuid) is null or company_id = :company)
				  and (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:station as uuid) is null or station_id = :station)
				  and (cast(:vehicle as uuid) is null or vehicle_id = :vehicle)
				  and (cast(:device as uuid) is null or device_id = :device)
				  and (cast(:agent as uuid) is null or agent_id = :agent)
				group by 1
				""")
			.param("start", ts(start))
			.param("end", ts(end))
			.param("company", subject.companyId())
			.param("operator", subject.operatorId())
			.param("station", subject.stationId())
			.param("vehicle", subject.vehicleId())
			.param("device", subject.deviceId())
			.param("agent", subject.agentId())
			.query((rs) -> {
				long[] row = byDay.get(rs.getObject("day", LocalDate.class));
				if (row != null) {
					row[0] = rs.getLong("completed");
					row[1] = rs.getLong("declined");
					row[2] = rs.getLong("offline");
					row[3] = rs.getLong("conflicted");
					row[4] = rs.getLong("volume");
					row[5] = rs.getLong("amount");
				}
			});
		// Alerts have no operator or agent column; for those subjects they are counted through the
		// station or device they name. Row-level security hides what the caller may not see.
		jdbc.sql("""
				select (created_at at time zone 'Africa/Cairo')::date as day, count(*) as alerts
				from alerts
				where created_at >= :start and created_at < :end
				  and (cast(:company as uuid) is null or company_id = :company)
				  and (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:station as uuid) is null or station_id = :station)
				  and (cast(:vehicle as uuid) is null or vehicle_id = :vehicle)
				  and (cast(:device as uuid) is null or device_id = :device)
				  and cast(:agent as uuid) is null
				group by 1
				""")
			.param("start", ts(start))
			.param("end", ts(end))
			.param("company", subject.companyId())
			.param("operator", subject.operatorId())
			.param("station", subject.stationId())
			.param("vehicle", subject.vehicleId())
			.param("device", subject.deviceId())
			.param("agent", subject.agentId())
			.query((rs) -> {
				long[] row = byDay.get(rs.getObject("day", LocalDate.class));
				if (row != null) {
					row[6] = rs.getLong("alerts");
				}
			});
		List<ActivityDay> days = new ArrayList<>();
		byDay.forEach((day, row) -> days.add(new ActivityDay(day, (int) row[0], (int) row[1], (int) row[2], (int) row[3],
				row[4], row[5], (int) row[6])));
		return days;
	}

}
