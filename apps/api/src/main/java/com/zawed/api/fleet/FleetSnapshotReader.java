package com.zawed.api.fleet;

import static com.zawed.api.platform.persistence.Sql.instant;

import java.security.GeneralSecurityException;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Component;

import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.crypto.DataEncryptor;
import com.zawed.api.rules.FuelingRules.LimitKind;
import com.zawed.api.rules.Periods.Period;

@Component
class FleetSnapshotReader implements FleetSnapshot {

	private final JdbcClient jdbc;
	private final DataEncryptor encryptor;

	FleetSnapshotReader(JdbcClient jdbc, DataEncryptor encryptor) {
		this.jdbc = jdbc;
		this.encryptor = encryptor;
	}

	@Override
	public Fleet snapshot(byte[] digestKey) {
		Mac mac = hmac(digestKey);
		List<SnapshotTag> tags = new ArrayList<>();
		Map<UUID, SnapshotVehicle> vehicles = new LinkedHashMap<>();
		// A tag that can fuel, or that is waiting for its replacement (the device then says why it is
		// refused); its vehicle not decommissioned.
		jdbc.sql("""
				select t.id, t.vehicle_id, t.uid_encrypted, t.status, t.cooldown_until, v.company_id, v.plate,
				       v.fuel_type, v.tier, v.tank_capacity_ml, v.status as vehicle_status
				from rfid_tags t join vehicles v on v.id = t.vehicle_id
				where t.status in ('ACTIVE', 'REPLACEMENT_PENDING') and v.status <> 'DECOMMISSIONED'
				""").query((rs) -> {
			UUID tagId = rs.getObject("id", UUID.class);
			UUID vehicleId = rs.getObject("vehicle_id", UUID.class);
			byte[] uid = encryptor.decrypt(rs.getBytes("uid_encrypted"), Tags.associatedData(tagId));
			String digest = HexFormat.of().formatHex(mac.doFinal(uid));
			tags.add(new SnapshotTag(digest, tagId, vehicleId, rs.getString("status"), instant(rs, "cooldown_until")));
			vehicles.putIfAbsent(vehicleId, new SnapshotVehicle(vehicleId, rs.getObject("company_id", UUID.class),
					rs.getString("plate"), FuelType.valueOf(rs.getString("fuel_type")), "VIP".equals(rs.getString("tier")),
					rs.getLong("tank_capacity_ml"), "ACTIVE".equals(rs.getString("vehicle_status"))));
		});
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

}
