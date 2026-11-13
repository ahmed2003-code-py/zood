package com.zawed.api.network;

import static com.zawed.api.platform.persistence.Sql.clean;
import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.network.Operators.Station;
import com.zawed.api.network.Operators.StationStatus;
import com.zawed.api.audit.AuditLog;
import com.zawed.api.platform.crypto.LookupHasher;
import com.zawed.api.platform.crypto.SecureTokens;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * POS devices (PRD §6.3 Register POS Device, §7.1). A device is registered to one station and starts
 * PENDING_ACTIVATION with a one-time activation code that Ops reads to the installer. The device
 * redeems it in B5 to receive its own credentials.
 *
 * <p>The activation code is 12 characters from a 31-letter alphabet (about 59 bits), valid for 72
 * hours and for five attempts. With that much entropy a keyed hash is enough to store it (an
 * offline guess against the hash is impossible without the server key) and it lets the device be
 * found by its code. It is shown once and can only be replaced, never read back.
 */
@Service
class Devices {

	enum DeviceStatus {

		PENDING_ACTIVATION, ACTIVE, REVOKED

	}

	record Device(UUID id, UUID operatorId, UUID stationId, String code, String label, DeviceStatus status,
			Instant activationExpiresAt, Instant boundAt, Instant lastSyncAt, Instant revokedAt, String revokedReason,
			Instant createdAt) {

		static Device map(ResultSet rs, int row) throws SQLException {
			return new Device(rs.getObject("id", UUID.class), rs.getObject("operator_id", UUID.class),
					rs.getObject("station_id", UUID.class), rs.getString("code"), rs.getString("label"),
					DeviceStatus.valueOf(rs.getString("status")), instant(rs, "activation_expires_at"),
					instant(rs, "bound_at"), instant(rs, "last_sync_at"), instant(rs, "revoked_at"),
					rs.getString("revoked_reason"), instant(rs, "created_at"));
		}

	}

	/** A device with the activation code that was just generated for it. The code is not stored. */
	record Registered(Device device, String activationCode) {
	}

	static final String ACTIVATION_NAMESPACE = "pos-activation";
	static final Duration ACTIVATION_TTL = Duration.ofHours(72);
	static final int ACTIVATION_CODE_LENGTH = 12;
	private static final int DEVICE_CODE_LENGTH = 8;
	private static final int CODE_ATTEMPTS = 5;

	private final JdbcClient jdbc;
	private final Operators operators;
	private final LookupHasher lookup;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;

	Devices(JdbcClient jdbc, Operators operators, LookupHasher lookup, AuditLog audit, IdGenerator ids, Clock clock) {
		this.jdbc = jdbc;
		this.operators = operators;
		this.lookup = lookup;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
	}

	@Transactional(readOnly = true)
	Page<Device> list(UUID operatorId, UUID stationId, DeviceStatus status, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Device> rows = jdbc.sql("""
				select * from pos_devices
				where (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:station as uuid) is null or station_id = :station)
				  and (cast(:status as text) is null or status = :status)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("operator", operatorId)
			.param("station", stationId)
			.param("status", status == null ? null : status.name())
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Device::map)
			.list();
		return Page.of(rows, size, Device::id);
	}

	@Transactional(readOnly = true)
	Device get(UUID id) {
		return jdbc.sql("select * from pos_devices where id = :id")
			.param("id", id)
			.query(Device::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Device"));
	}

	@Transactional
	Registered register(ZawedPrincipal actor, UUID stationId, String label) {
		Station station = operators.lockStation(stationId);
		if (station.status() == StationStatus.CLOSED) {
			throw new ApiException(HttpStatus.CONFLICT, "STATION_CLOSED", "The station is closed");
		}
		Instant now = clock.instant();
		String activationCode = SecureTokens.code(ACTIVATION_CODE_LENGTH);
		UUID id = ids.next();
		String code = null;
		for (int attempt = 0; attempt < CODE_ATTEMPTS && code == null; attempt++) {
			String candidate = "POS-" + SecureTokens.code(DEVICE_CODE_LENGTH);
			if (Boolean.FALSE.equals(jdbc.sql("select exists (select 1 from pos_devices where code = :code)")
				.param("code", candidate)
				.query(Boolean.class)
				.single())) {
				code = candidate;
			}
		}
		if (code == null) {
			throw new IllegalStateException("Could not generate a unique device code");
		}
		try {
			jdbc.sql("""
					insert into pos_devices (id, operator_id, station_id, code, label, status, activation_code_hash,
					                         activation_expires_at, created_at, created_by, updated_at, updated_by)
					values (:id, :operator, :station, :code, :label, 'PENDING_ACTIVATION', :hash, :expires, :now,
					        :actor, :now, :actor)
					""")
				.param("id", id)
				.param("operator", station.operatorId())
				.param("station", stationId)
				.param("code", code)
				.param("label", clean(label))
				.param("hash", lookup.hash(ACTIVATION_NAMESPACE, activationCode))
				.param("expires", ts(now.plus(ACTIVATION_TTL)))
				.param("now", ts(now))
				.param("actor", actor.userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw new ApiException(HttpStatus.CONFLICT, "CONCURRENT_CHANGE", "Try again");
		}
		audit.record(Operators.actorOf(actor), "DEVICE_REGISTERED", "POS_DEVICE", id, Scope.operator(station.operatorId()),
				Map.of("code", code, "stationId", stationId.toString()));
		return new Registered(get(id), activationCode);
	}

	/** A fresh activation code for a device that has not been activated yet; the old one stops working. */
	@Transactional
	Registered reissueActivation(ZawedPrincipal actor, UUID id) {
		Device device = lock(id);
		if (device.status() != DeviceStatus.PENDING_ACTIVATION) {
			throw new ApiException(HttpStatus.CONFLICT, "DEVICE_NOT_PENDING",
					"Only a device waiting for activation gets a new code; revoke and register a new device instead");
		}
		Instant now = clock.instant();
		String activationCode = SecureTokens.code(ACTIVATION_CODE_LENGTH);
		jdbc.sql("""
				update pos_devices set activation_code_hash = :hash, activation_expires_at = :expires,
				       activation_attempts = 0, updated_at = :now, updated_by = :actor
				where id = :id
				""")
			.param("id", id)
			.param("hash", lookup.hash(ACTIVATION_NAMESPACE, activationCode))
			.param("expires", ts(now.plus(ACTIVATION_TTL)))
			.param("now", ts(now))
			.param("actor", actor.userId())
			.update();
		audit.record(Operators.actorOf(actor), "DEVICE_ACTIVATION_REISSUED", "POS_DEVICE", id,
				Scope.operator(device.operatorId()), Map.of());
		return new Registered(get(id), activationCode);
	}

	/** Final: a revoked device can never sync or fuel again (PRD §7.1 lost or stolen device). */
	@Transactional
	Device revoke(ZawedPrincipal actor, UUID id, String reason) {
		Device device = lock(id);
		if (device.status() == DeviceStatus.REVOKED) {
			return device;
		}
		Instant now = clock.instant();
		jdbc.sql("""
				update pos_devices set status = 'REVOKED', activation_code_hash = null, activation_expires_at = null,
				       revoked_at = :now, revoked_reason = :reason, updated_at = :now, updated_by = :actor
				where id = :id
				""").param("id", id).param("reason", reason).param("now", ts(now)).param("actor", actor.userId()).update();
		audit.record(Operators.actorOf(actor), "DEVICE_REVOKED", "POS_DEVICE", id, Scope.operator(device.operatorId()),
				Map.of("from", device.status().name(), "reason", reason));
		return get(id);
	}

	private Device lock(UUID id) {
		return jdbc.sql("select * from pos_devices where id = :id for update")
			.param("id", id)
			.query(Device::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Device"));
	}

}
