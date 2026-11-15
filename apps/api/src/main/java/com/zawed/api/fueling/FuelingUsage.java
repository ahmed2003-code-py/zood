package com.zawed.api.fueling;

import java.time.Instant;
import java.util.Map;
import java.util.UUID;

/**
 * What each vehicle has used in its current periods, for the POS snapshot (B5 contract §2). Read
 * without locks: the device uses it as a starting point and the engine decides again at sync.
 * Runs in the caller's transaction and scope.
 */
public interface FuelingUsage {

	/** Completed fuelings since each period start, in millilitres and piastres. */
	record Usage(long dayMl, long weekMl, long monthMl, long dayPiastres, long weekPiastres, long monthPiastres) {

		public static final Usage NONE = new Usage(0, 0, 0, 0, 0, 0);

	}

	/** Every vehicle with a completed fueling since the earliest of the three starts. */
	Map<UUID, Usage> usageSince(Instant dayStart, Instant weekStart, Instant monthStart);

}
