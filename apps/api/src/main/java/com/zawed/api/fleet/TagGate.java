package com.zawed.api.fleet;

import java.time.Instant;
import java.util.List;
import java.util.Optional;
import java.util.UUID;

import com.zawed.api.platform.FuelType;
import com.zawed.api.rules.FuelingRules.LimitKind;
import com.zawed.api.rules.Periods.Period;

/**
 * What the fueling engine asks of the fleet at the pump (B4 contract §3). Every call runs in the
 * engine's transaction and scope. Lock order is the company wallet, then the tag: {@link #lock} is
 * called only once the engine holds the wallet.
 */
public interface TagGate {

	/** A tag, its vehicle and its company, as read under the tag's row lock. */
	record TagAtPump(UUID tagId, UUID vehicleId, UUID companyId, boolean tagActive, Instant cooldownUntil,
			String plate, FuelType vehicleFuel, boolean vehicleActive, boolean vip, long tankCapacityMl,
			Long vipMonthlyCeilingMl) {
	}

	/** An active limit rule of a vehicle, in its own unit (millilitres or piastres). */
	record LimitRule(LimitKind kind, Period period, long maxValue) {
	}

	/** The tag and company a scanned UID names, without locking. Empty for a UID never bound. */
	Optional<Bound> find(String rawUid);

	record Bound(UUID tagId, UUID companyId) {
	}

	/** Locks the tag row and reads it with its vehicle. */
	TagAtPump lock(UUID tagId);

	List<LimitRule> activeLimits(UUID vehicleId);

	/** Starts the cooling-off period after an approved fueling (PRD 6.4), mirrored from the tag write. */
	void startCooldown(UUID tagId, Instant until);

}
