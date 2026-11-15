package com.zawed.api.sync;

import static com.zawed.api.platform.persistence.Sql.instant;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Duration;
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
 * The sync monitor (B5 contract §5): when each active device last reached the server, what its last
 * batch did, and what it said it still holds. Runs in the caller's scope: Operations sees every
 * device, a station manager the operator's own (row-level security on devices and batches).
 */
@Service
class SyncMonitor {

	/** A device not heard from for longer than this is flagged. */
	static final Duration STALE_AFTER = Duration.ofHours(24);

	record DeviceSync(
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID deviceId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String code, String label,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID operatorId,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String stationNameAr,
			@Schema(requiredMode = RequiredMode.REQUIRED) String stationNameEn, Instant lastSyncAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean stale, LastSyncBatch lastBatch) {
	}

	record LastSyncBatch(
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant receivedAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) int items,
			@Schema(requiredMode = RequiredMode.REQUIRED) int recorded,
			@Schema(requiredMode = RequiredMode.REQUIRED) int duplicates,
			@Schema(requiredMode = RequiredMode.REQUIRED) int rejected,
			@Schema(requiredMode = RequiredMode.REQUIRED) int withConflicts,
			@Schema(requiredMode = RequiredMode.REQUIRED) int pending) {
	}

	record SyncBatch(
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID deviceBatchId,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID deviceId,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant receivedAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) int items,
			@Schema(requiredMode = RequiredMode.REQUIRED) int recorded,
			@Schema(requiredMode = RequiredMode.REQUIRED) int duplicates,
			@Schema(requiredMode = RequiredMode.REQUIRED) int rejected,
			@Schema(requiredMode = RequiredMode.REQUIRED) int withConflicts,
			@Schema(requiredMode = RequiredMode.REQUIRED) int pending) {

		static SyncBatch map(ResultSet rs, int row) throws SQLException {
			return new SyncBatch(rs.getObject("id", UUID.class), rs.getObject("device_batch_id", UUID.class),
					rs.getObject("device_id", UUID.class), rs.getObject("station_id", UUID.class),
					instant(rs, "received_at"), rs.getInt("item_count"), rs.getInt("recorded"), rs.getInt("duplicates"),
					rs.getInt("rejected"), rs.getInt("with_conflicts"), rs.getInt("pending_reported"));
		}

	}

	private final JdbcClient jdbc;
	private final Clock clock;

	SyncMonitor(JdbcClient jdbc, Clock clock) {
		this.jdbc = jdbc;
		this.clock = clock;
	}

	/** Active devices, those never seen or seen longest ago first. */
	@Transactional(readOnly = true)
	Page<DeviceSync> devices(UUID operatorId, UUID stationId, Boolean staleOnly, String cursor, Integer limit) {
		int size = Page.limit(limit);
		Instant staleBefore = clock.instant().minus(STALE_AFTER);
		List<DeviceSync> rows = jdbc.sql("""
				select d.id, d.code, d.label, d.operator_id, d.station_id, s.name_ar, s.name_en, d.last_sync_at,
				       b.received_at, b.item_count, b.recorded, b.duplicates, b.rejected, b.with_conflicts,
				       b.pending_reported
				from pos_devices d
				join stations s on s.id = d.station_id
				left join lateral (
				  select * from sync_batches sb where sb.device_id = d.id order by sb.id desc limit 1
				) b on true
				where d.status = 'ACTIVE'
				  and (cast(:operator as uuid) is null or d.operator_id = :operator)
				  and (cast(:station as uuid) is null or d.station_id = :station)
				  and (not :staleOnly or d.last_sync_at is null or d.last_sync_at < :staleBefore)
				  and (cast(:cursor as uuid) is null or d.id < :cursor)
				order by d.id desc limit :limit
				""")
			.param("operator", operatorId)
			.param("station", stationId)
			.param("staleOnly", Boolean.TRUE.equals(staleOnly))
			.param("staleBefore", java.sql.Timestamp.from(staleBefore))
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query((rs, n) -> {
				Instant lastSync = instant(rs, "last_sync_at");
				Instant batchAt = instant(rs, "received_at");
				LastSyncBatch last = batchAt == null ? null
						: new LastSyncBatch(batchAt, rs.getInt("item_count"), rs.getInt("recorded"), rs.getInt("duplicates"),
								rs.getInt("rejected"), rs.getInt("with_conflicts"), rs.getInt("pending_reported"));
				return new DeviceSync(rs.getObject("id", UUID.class), rs.getString("code"), rs.getString("label"),
						rs.getObject("operator_id", UUID.class), rs.getObject("station_id", UUID.class),
						rs.getString("name_ar"), rs.getString("name_en"), lastSync,
						lastSync == null || lastSync.isBefore(staleBefore), last);
			})
			.list();
		return Page.of(rows, size, DeviceSync::deviceId);
	}

	@Transactional(readOnly = true)
	Page<SyncBatch> batches(UUID operatorId, UUID deviceId, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<SyncBatch> rows = jdbc.sql("""
				select * from sync_batches
				where (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:device as uuid) is null or device_id = :device)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("operator", operatorId)
			.param("device", deviceId)
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(SyncBatch::map)
			.list();
		return Page.of(rows, size, SyncBatch::id);
	}

}
