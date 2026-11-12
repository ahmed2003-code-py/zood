package com.zawed.api.platform.ids;

import java.security.SecureRandom;
import java.time.Clock;
import java.util.UUID;

import org.springframework.stereotype.Component;

@Component
class UuidV7Generator implements IdGenerator {

	private final Clock clock;
	private final SecureRandom random = new SecureRandom();

	UuidV7Generator(Clock clock) {
		this.clock = clock;
	}

	@Override
	public UUID next() {
		return UuidV7.create(clock.millis(), random);
	}

}
