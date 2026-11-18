package com.zawed.api.network;

import static com.zawed.api.platform.persistence.Sql.instant;

import java.time.Clock;
import java.time.Instant;
import java.util.List;
import java.util.UUID;

import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.platform.web.Page;

/**
 * Shifts (plan §7 B6): an agent's session on a device, from sign-in to sign-out, revocation or
 * expiry. They are what "last shift" means in the quick period filters and on the calendar. Read
 * in the caller's scope: Operations sees all, a station manager the operator's own (V10).
 */
@Service
class Shifts {

	record Shift(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID agentId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String workerId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String agentName,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID deviceId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String deviceCode,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant startedAt,
			@Schema(description = "When it ended; null while it is still open") Instant endedAt,
			@Schema(description = "SIGNED_OUT, SIGNED_IN_ELSEWHERE, EXPIRED, … ; null while open") String endReason) {
	}

	private final JdbcClient jdbc;
	private final Clock clock;

	Shifts(JdbcClient jdbc, Clock clock) {
		this.jdbc = jdbc;
		this.clock = clock;
	}

	/** Shifts newest first, filtered by operator, station, device or agent. */
	@Transactional(readOnly = true)
	Page<Shift> list(UUID operatorId, UUID stationId, UUID deviceId, UUID agentId, String cursor, Integer limit) {
		int size = Page.limit(limit);
		Instant now = clock.instant();
		List<Shift> rows = jdbc.sql("""
				select s.id, s.agent_id, a.worker_id, a.full_name, s.device_id, d.code, s.station_id, s.created_at,
				       s.revoked_at, s.revoke_reason, s.expires_at
				from agent_sessions s
				join agents a on a.id = s.agent_id
				join pos_devices d on d.id = s.device_id
				where (cast(:operator as uuid) is null or s.operator_id = :operator)
				  and (cast(:station as uuid) is null or s.station_id = :station)
				  and (cast(:device as uuid) is null or s.device_id = :device)
				  and (cast(:agent as uuid) is null or s.agent_id = :agent)
				  and (cast(:cursor as uuid) is null or s.id < :cursor)
				order by s.created_at desc, s.id desc limit :limit
				""")
			.param("operator", operatorId)
			.param("station", stationId)
			.param("device", deviceId)
			.param("agent", agentId)
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query((rs, n) -> {
				Instant revoked = instant(rs, "revoked_at");
				Instant expires = instant(rs, "expires_at");
				Instant ended = revoked != null ? revoked : expires.isAfter(now) ? null : expires;
				String reason = revoked != null ? rs.getString("revoke_reason") : ended != null ? "EXPIRED" : null;
				return new Shift(rs.getObject("id", UUID.class), rs.getObject("agent_id", UUID.class),
						rs.getString("worker_id"), rs.getString("full_name"), rs.getObject("device_id", UUID.class),
						rs.getString("code"), rs.getObject("station_id", UUID.class), instant(rs, "created_at"), ended, reason);
			})
			.list();
		return Page.of(rows, size, Shift::id);
	}

}
