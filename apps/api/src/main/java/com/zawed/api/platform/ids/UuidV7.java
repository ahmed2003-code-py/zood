package com.zawed.api.platform.ids;

import java.util.UUID;
import java.util.random.RandomGenerator;

/**
 * UUID version 7 (RFC 9562): a 48-bit Unix millisecond timestamp followed by random bits. Ids
 * created later sort later, which keeps primary-key indexes compact, and a POS device can create
 * them offline without asking the server (ADR 5).
 *
 * <p>Two ids created in the same millisecond are ordered by their random bits, so their relative
 * order is arbitrary. Nothing may rely on id order as the order of events.
 */
public final class UuidV7 {

	private static final long TIMESTAMP_MASK = 0xFFFF_FFFF_FFFFL;
	private static final long VERSION_7 = 0x7000L;
	private static final long VARIANT_RFC = 0x8000_0000_0000_0000L;

	private UuidV7() {
	}

	public static UUID create(long epochMillis, RandomGenerator random) {
		if (epochMillis < 0 || epochMillis > TIMESTAMP_MASK) {
			throw new IllegalArgumentException("timestamp does not fit in 48 bits: " + epochMillis);
		}
		long mostSignificant = (epochMillis << 16) | VERSION_7 | random.nextInt(1 << 12);
		long leastSignificant = (random.nextLong() & 0x3FFF_FFFF_FFFF_FFFFL) | VARIANT_RFC;
		return new UUID(mostSignificant, leastSignificant);
	}

	/** The Unix millisecond timestamp carried by a version 7 id. */
	public static long timestampOf(UUID id) {
		if (id.version() != 7) {
			throw new IllegalArgumentException("not a version 7 UUID: " + id);
		}
		return id.getMostSignificantBits() >>> 16;
	}

}
