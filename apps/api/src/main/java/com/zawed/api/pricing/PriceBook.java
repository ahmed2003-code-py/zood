package com.zawed.api.pricing;

import java.time.Instant;
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

}
