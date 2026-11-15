package com.zawed.api.pricing;

import java.time.Instant;
import java.util.List;
import java.util.Optional;
import java.util.UUID;

import com.zawed.api.platform.FuelType;

/**
 * The price of a litre at a station at an instant, for the fueling engine and receipts. Reads run in
 * the caller's transaction; every signed-in scope may read prices.
 */
public interface PriceBook {

	/** A price and where it came from. {@code stationId} is null for the national price. */
	record Price(FuelType fuelType, UUID stationId, long pricePiastresPerLitre, Instant effectiveFrom) {
	}

	/**
	 * The price in force: of the national price and the station's own override, the one that took
	 * effect last. A national price change therefore supersedes an older override.
	 */
	Optional<Price> priceAt(UUID stationId, FuelType fuelType, Instant at);

	/**
	 * What a POS needs to apply {@link #priceAt}'s rule itself until {@code until}: the national
	 * price and the station's override in force at {@code now}, and every change of either that
	 * takes effect after {@code now} and no later than {@code until}, oldest first.
	 */
	List<Price> entriesFor(UUID stationId, FuelType fuelType, Instant now, Instant until);

}
