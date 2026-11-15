package com.zawed.api.fleet;

import java.time.Instant;
import java.util.List;
import java.util.UUID;

import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;

import com.zawed.api.platform.FuelType;
import com.zawed.api.rules.FuelingRules.LimitKind;
import com.zawed.api.rules.Periods.Period;

/**
 * The fleet as a POS needs it offline (B5 contract §2): every tag that can fuel, its vehicle and the
 * vehicle's limits. A tag is identified by a digest of its UID under a key given to one device, never
 * by the UID itself. Runs in the caller's transaction and scope.
 */
public interface FleetSnapshot {

	record SnapshotTag(
			@Schema(requiredMode = RequiredMode.REQUIRED) String digest,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID tagId,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID vehicleId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String status, Instant cooldownUntil) {
	}

	record SnapshotVehicle(
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID companyId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String plate,
			@Schema(requiredMode = RequiredMode.REQUIRED) FuelType fuelType,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean vip,
			@Schema(requiredMode = RequiredMode.REQUIRED) long tankCapacityMl,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean active) {
	}

	record SnapshotLimit(UUID vehicleId, LimitKind kind, Period period, long maxValue) {
	}

	record Fleet(List<SnapshotTag> tags, List<SnapshotVehicle> vehicles, List<SnapshotLimit> limits) {
	}

	/**
	 * @param digestKey the device's key: each tag's digest is HMAC-SHA256(digestKey, normalised UID)
	 * in lower-case hex
	 */
	Fleet snapshot(byte[] digestKey);

}
