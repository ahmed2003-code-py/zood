package com.zawed.api.platform.ids;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.security.SecureRandom;
import java.util.HashSet;
import java.util.Set;
import java.util.UUID;

import org.junit.jupiter.api.Test;

class UuidV7Tests {

	private final SecureRandom random = new SecureRandom();

	@Test
	void isAVersion7Rfc9562Uuid() {
		UUID id = UuidV7.create(1_794_000_000_000L, random);

		assertThat(id.version()).isEqualTo(7);
		assertThat(id.variant()).isEqualTo(2);
	}

	@Test
	void carriesItsTimestamp() {
		long millis = 1_794_000_123_456L;

		assertThat(UuidV7.timestampOf(UuidV7.create(millis, random))).isEqualTo(millis);
	}

	@Test
	void sortsByCreationTimeAsText() {
		UUID earlier = UuidV7.create(1_794_000_000_000L, random);
		UUID later = UuidV7.create(1_794_000_000_001L, random);

		// PostgreSQL compares uuid values byte by byte, which is the order of the text form.
		assertThat(earlier.toString()).isLessThan(later.toString());
	}

	@Test
	void doesNotRepeatWithinOneMillisecond() {
		Set<UUID> ids = new HashSet<>();
		for (int i = 0; i < 10_000; i++) {
			ids.add(UuidV7.create(1_794_000_000_000L, random));
		}

		assertThat(ids).hasSize(10_000);
	}

	@Test
	void rejectsATimestampThatDoesNotFit() {
		assertThatThrownBy(() -> UuidV7.create(-1, random)).isInstanceOf(IllegalArgumentException.class);
		assertThatThrownBy(() -> UuidV7.create(1L << 48, random)).isInstanceOf(IllegalArgumentException.class);
	}

	@Test
	void readsATimestampOnlyFromAVersion7Id() {
		assertThatThrownBy(() -> UuidV7.timestampOf(UUID.randomUUID())).isInstanceOf(IllegalArgumentException.class);
	}

}
