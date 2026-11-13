package com.zawed.api.network;

import java.time.ZoneId;
import java.util.Optional;
import java.util.Set;
import java.util.UUID;

import com.zawed.api.platform.FuelType;

/** What other modules may ask about a station. Reads run in the caller's transaction and scope. */
public interface StationDirectory {

	/** A station as the fueling engine and receipts need it. Rules are evaluated in {@code zone}. */
	record StationRef(UUID id, UUID operatorId, String nameAr, String nameEn, ZoneId zone, Set<FuelType> fuelTypes,
			boolean active) {
	}

	Optional<StationRef> findStation(UUID stationId);

}
