package com.zawed.api.fleet;

import static com.zawed.api.platform.persistence.Sql.instant;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Component;

import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.crypto.DataEncryptor;
import com.zawed.api.platform.crypto.LookupHasher;
import com.zawed.api.rules.FuelingRules.LimitKind;
import com.zawed.api.rules.Periods.Period;

@Component
class FleetSnapshotReader implements FleetSnapshot {

	private static final Logger LOG = LoggerFactory.getLogger(FleetSnapshotReader.class);

	private final JdbcClient jdbc;
	private final DataEncryptor encryptor;
	private final LookupHasher lookup;

	FleetSnapshotReader(JdbcClient jdbc, DataEncryptor encryptor, LookupHasher lookup) {
		this.jdbc = jdbc;
		this.encryptor = encryptor;
		this.lookup = lookup;
	}

	@Override
	public Fleet snapshot(byte[] digestKey) {
		Mac mac = hmac(digestKey);
		List<SnapshotTag> tags = new ArrayList<>();
		Map<UUID, SnapshotVehicle> vehicles = new LinkedHashMap<>();
		List<UUID> unreadable = new ArrayList<>();
		List<UUID> mismatched = new ArrayList<>();
		// A tag that can fuel, or that is waiting for its replacement (the device then says why it is
		// refused); its vehicle not decommissioned.
		jdbc.sql("""
				select t.id, t.vehicle_id, t.uid_encrypted, t.uid_lookup, t.status, t.cooldown_until, v.company_id,
				       v.plate, v.fuel_type, v.tier, v.tank_capacity_ml, v.status as vehicle_status
				from rfid_tags t join vehicles v on v.id = t.vehicle_id
				where t.status in ('ACTIVE', 'REPLACEMENT_PENDING') and v.status <> 'DECOMMISSIONED'
				""").query((rs) -> {
			UUID tagId = rs.getObject("id", UUID.class);
			UUID vehicleId = rs.getObject("vehicle_id", UUID.class);
			byte[] uid;
			try {
				uid = encryptor.decrypt(rs.getBytes("uid_encrypted"), Tags.associatedData(tagId));
			}
			catch (IllegalArgumentException ex) {
				// A key this instance does not hold, or a value that does not verify: that tag cannot be
				// fueled offline, but every other device still gets every other tag.
				unreadable.add(tagId);
				return;
			}
			// The ciphertext must be the UID the tag was issued with. uid_lookup is made with the lookup key,
			// which neither a data key nor the runtime role can change (V14, V20), so a UID swapped in
			// under a data key, or a lookup key that is not the original, never reaches a device.
			if (!MessageDigest.isEqual(lookup.hash(Tags.LOOKUP_NAMESPACE, new String(uid, StandardCharsets.US_ASCII))
				.getBytes(StandardCharsets.US_ASCII), rs.getString("uid_lookup").getBytes(StandardCharsets.US_ASCII))) {
				mismatched.add(tagId);
				return;
			}
			if (uid.length < SHORTEST_UID_IN_SNAPSHOT) {
				return;
			}
			String digest = HexFormat.of().formatHex(mac.doFinal(uid)).substring(0, DIGEST_HEX_LENGTH);
			tags.add(new SnapshotTag(digest, vehicleId, rs.getString("status"), instant(rs, "cooldown_until")));
			vehicles.putIfAbsent(vehicleId, new SnapshotVehicle(vehicleId, rs.getObject("company_id", UUID.class),
					rs.getString("plate"), FuelType.valueOf(rs.getString("fuel_type")), "VIP".equals(rs.getString("tier")),
					rs.getLong("tank_capacity_ml"), "ACTIVE".equals(rs.getString("vehicle_status"))));
		});
		if (!unreadable.isEmpty() || !mismatched.isEmpty()) {
			// Ids and counts only, never a value. An ERROR, so it reaches Sentry once SENTRY_DSN is set.
			LOG.error("Offline snapshot: {} tags left out; {} could not be decrypted {}, {} do not match their lookup "
					+ "hash {}", unreadable.size() + mismatched.size(), unreadable.size(), firstIds(unreadable),
					mismatched.size(), firstIds(mismatched));
		}
		List<SnapshotLimit> limits = new ArrayList<>();
		jdbc.sql("select vehicle_id, kind, period, max_value from limit_rules where active").query((rs) -> {
			UUID vehicleId = rs.getObject("vehicle_id", UUID.class);
			if (vehicles.containsKey(vehicleId)) {
				limits.add(new SnapshotLimit(vehicleId, LimitKind.valueOf(rs.getString("kind")),
						Period.valueOf(rs.getString("period")), rs.getLong("max_value")));
			}
		});
		return new Fleet(List.copyOf(tags), List.copyOf(vehicles.values()), List.copyOf(limits));
	}

	private static Mac hmac(byte[] key) {
		try {
			Mac mac = Mac.getInstance("HmacSHA256");
			mac.init(new SecretKeySpec(key, "HmacSHA256"));
			return mac;
		}
		catch (GeneralSecurityException ex) {
			throw new IllegalStateException(ex);
		}
	}


	/** At most {@value #LOGGED_IDS} ids per log line, so a retired key cannot flood the log on every device poll. */
	private static String firstIds(List<UUID> ids) {
		if (ids.size() <= LOGGED_IDS) {
			return ids.toString();
		}
		return ids.subList(0, LOGGED_IDS) + " and " + (ids.size() - LOGGED_IDS) + " more";
	}

	private static final int LOGGED_IDS = 20;
}
