package com.zawed.api.fleet;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.nio.charset.StandardCharsets;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Timestamp;
import java.time.Clock;
import java.time.Instant;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.regex.Pattern;

import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.fleet.Vehicles.Vehicle;
import com.zawed.api.platform.crypto.DataEncryptor;
import com.zawed.api.platform.crypto.LookupHasher;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;

/**
 * RFID tags (PRD §4.4, §7.3). A tag is the vehicle's key at the pump, so it is handled like a
 * credential:
 *
 * <ul>
 * <li>The UID is never stored in clear. It is kept encrypted (AES-GCM, bound to the tag id) and as a
 * keyed hash that finds it; responses show the last four characters only.
 * <li>A physical tag is bound once, ever. A lost or stolen tag cannot be bound again, to this vehicle
 * or any other.
 * <li>A vehicle has at most one active tag. Replacing a tag carries the cooldown over, so swapping
 * tags cannot be used to fuel again before the cooling-off period ends.
 * <li>Lost, stolen and deactivated are final.
 * </ul>
 */
@Service
class Tags {

	enum TagStatus {

		ACTIVE, LOST, STOLEN, DEACTIVATED, REPLACEMENT_PENDING

	}

	record Tag(UUID id, UUID vehicleId, String uidLast4, TagStatus status, String statusReason, Instant cooldownUntil,
			Instant createdAt) {

		static Tag map(ResultSet rs, int row) throws SQLException {
			return new Tag(rs.getObject("id", UUID.class), rs.getObject("vehicle_id", UUID.class),
					rs.getString("uid_last4"), TagStatus.valueOf(rs.getString("status")), rs.getString("status_reason"),
					instant(rs, "cooldown_until"), instant(rs, "created_at"));
		}

	}

	/** Hex UIDs from 4-byte to 10-byte tags (ISO 14443 single, double and triple size) and EPC-96. */
	private static final Pattern UID = Pattern.compile("[0-9A-F]{8,32}");

	static final String LOOKUP_NAMESPACE = "rfid-uid";

	private static final Map<TagStatus, Set<TagStatus>> TRANSITIONS = Map.of(TagStatus.ACTIVE,
			Set.of(TagStatus.LOST, TagStatus.STOLEN, TagStatus.DEACTIVATED, TagStatus.REPLACEMENT_PENDING),
			TagStatus.REPLACEMENT_PENDING, Set.of(TagStatus.ACTIVE, TagStatus.LOST, TagStatus.STOLEN, TagStatus.DEACTIVATED));

	private final JdbcClient jdbc;
	private final Vehicles vehicles;
	private final DataEncryptor encryptor;
	private final LookupHasher lookup;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;

	Tags(JdbcClient jdbc, Vehicles vehicles, DataEncryptor encryptor, LookupHasher lookup, AuditLog audit,
			IdGenerator ids, Clock clock) {
		this.jdbc = jdbc;
		this.vehicles = vehicles;
		this.encryptor = encryptor;
		this.lookup = lookup;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
	}

	@Transactional(readOnly = true)
	List<Tag> ofVehicle(UUID vehicleId) {
		vehicles.get(vehicleId);
		return jdbc.sql("select * from rfid_tags where vehicle_id = :vehicle order by id desc limit 100")
			.param("vehicle", vehicleId)
			.query(Tag::map)
			.list();
	}

	/**
	 * Binds a new tag to an active vehicle. A tag waiting for replacement is closed in the same
	 * transaction; an active tag must be reported first, so a working tag is never silently retired.
	 */
	@Transactional
	Tag issue(ZawedPrincipal actor, UUID vehicleId, String rawUid) {
		String uid = normalizeUid(rawUid);
		Vehicle vehicle = vehicles.lock(vehicleId);
		if (vehicle.status() != Vehicles.Status.ACTIVE) {
			throw new ApiException(HttpStatus.CONFLICT, "VEHICLE_NOT_ACTIVE", "Tags are issued to active vehicles only");
		}
		List<Tag> current = jdbc.sql("""
				select * from rfid_tags where vehicle_id = :vehicle and status in ('ACTIVE', 'REPLACEMENT_PENDING')
				""").param("vehicle", vehicleId).query(Tag::map).list();
		if (current.stream().anyMatch((tag) -> tag.status() == TagStatus.ACTIVE)) {
			throw new ApiException(HttpStatus.CONFLICT, "VEHICLE_HAS_ACTIVE_TAG",
					"Report the current tag lost, stolen or for replacement first");
		}
		String uidLookup = lookup.hash(LOOKUP_NAMESPACE, uid);
		if (Boolean.TRUE.equals(jdbc.sql("select exists (select 1 from rfid_tags where uid_lookup = :lookup)")
			.param("lookup", uidLookup)
			.query(Boolean.class)
			.single())) {
			throw tagAlreadyBound();
		}
		Instant now = clock.instant();
		// The cooldown belongs to the vehicle's last fueling, not to the plastic: carry it over.
		Timestamp cooldown = jdbc.sql("select max(cooldown_until) from rfid_tags where vehicle_id = :vehicle")
			.param("vehicle", vehicleId)
			.query(Timestamp.class)
			.optional()
			.orElse(null);
		jdbc.sql("""
				update rfid_tags set status = 'DEACTIVATED', status_reason = 'Replaced', updated_at = :now,
				       updated_by = :actor
				where vehicle_id = :vehicle and status = 'REPLACEMENT_PENDING'
				""").param("vehicle", vehicleId).param("now", ts(now)).param("actor", actor.userId()).update();
		UUID id = ids.next();
		try {
			jdbc.sql("""
					insert into rfid_tags (id, company_id, vehicle_id, uid_encrypted, uid_lookup, uid_last4, status,
					                       cooldown_until, created_at, created_by, updated_at, updated_by)
					values (:id, :company, :vehicle, :encrypted, :lookup, :last4, 'ACTIVE', :cooldown, :now, :actor,
					        :now, :actor)
					""")
				.param("id", id)
				.param("company", vehicle.companyId())
				.param("vehicle", vehicleId)
				.param("encrypted", encryptor.encrypt(uid.getBytes(StandardCharsets.US_ASCII), associatedData(id)))
				.param("lookup", uidLookup)
				.param("last4", uid.substring(uid.length() - 4))
				.param("cooldown", cooldown)
				.param("now", ts(now))
				.param("actor", actor.userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw tagAlreadyBound(); // a concurrent bind of the same tag
		}
		Map<String, Object> details = new HashMap<>();
		details.put("uidLast4", uid.substring(uid.length() - 4));
		details.put("replaced", current.size());
		audit.record(Vehicles.actorOf(actor), "TAG_ISSUED", "RFID_TAG", id, Scope.company(vehicle.companyId()),
				details);
		return get(id);
	}

	@Transactional
	Tag setStatus(ZawedPrincipal actor, UUID tagId, TagStatus status, String reason) {
		Tag tag = get(tagId);
		Vehicle vehicle = vehicles.lock(tag.vehicleId());
		tag = get(tagId); // read again under the vehicle lock
		if (tag.status() == status) {
			return tag;
		}
		if (!TRANSITIONS.getOrDefault(tag.status(), Set.of()).contains(status)) {
			throw new ApiException(HttpStatus.CONFLICT, "INVALID_TAG_TRANSITION",
					"A " + tag.status() + " tag cannot become " + status);
		}
		if (status == TagStatus.ACTIVE && vehicle.status() != Vehicles.Status.ACTIVE) {
			throw new ApiException(HttpStatus.CONFLICT, "VEHICLE_NOT_ACTIVE", "The vehicle is not active");
		}
		try {
			jdbc.sql("""
					update rfid_tags set status = :status, status_reason = :reason, updated_at = :now, updated_by = :actor
					where id = :id
					""")
				.param("id", tagId)
				.param("status", status.name())
				.param("reason", reason)
				.param("now", ts(clock.instant()))
				.param("actor", actor.userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw new ApiException(HttpStatus.CONFLICT, "VEHICLE_HAS_ACTIVE_TAG", "The vehicle already has an active tag");
		}
		audit.record(Vehicles.actorOf(actor), "TAG_STATUS_CHANGED", "RFID_TAG", tagId, Scope.company(vehicle.companyId()),
				Map.of("from", tag.status().name(), "to", status.name(), "reason", reason));
		return get(tagId);
	}

	private Tag get(UUID id) {
		return jdbc.sql("select * from rfid_tags where id = :id")
			.param("id", id)
			.query(Tag::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Tag"));
	}

	/** Readers print UIDs with spaces, colons or dashes, in either case. */
	static String normalizeUid(String raw) {
		String uid = raw == null ? "" : raw.replaceAll("[\\s:\\-]", "").toUpperCase(Locale.ROOT);
		if (!UID.matcher(uid).matches()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_TAG_UID", "A tag UID is 8 to 32 hex characters", "uid",
					null);
		}
		return uid;
	}

	private static byte[] associatedData(UUID tagId) {
		return ("rfid_tags.uid:" + tagId).getBytes(StandardCharsets.US_ASCII);
	}

	private static ApiException tagAlreadyBound() {
		return new ApiException(HttpStatus.CONFLICT, "TAG_ALREADY_BOUND", "This tag has been bound before", "uid", null);
	}

}
