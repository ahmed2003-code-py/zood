package com.zawed.api.platform.crypto;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.List;

import org.junit.jupiter.api.Test;

import com.zawed.api.platform.config.ZawedProperties;
import com.zawed.api.platform.config.ZawedProperties.Cors;
import com.zawed.api.platform.config.ZawedProperties.Environment;
import com.zawed.api.platform.config.ZawedProperties.Security;

/** The lookup hashes do not move when the data key rotates (ADR 0027). */
class LookupHasherTests {

	private static final String JWT = "a-real-looking-secret-for-tests-only-0123456789";
	private static final String KEY_1 = "bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=";
	private static final String KEY_2 = "bmV3LWRhdGEta2V5LWZvci1yb3RhdGlvbi10ZXN0cyE=";

	@Test
	void aRotatedDeploymentHashesExactlyAsBeforeTheRotation() {
		LookupHasher before = new LookupHasher(properties(new Security(JWT, KEY_1, null, null, null, null, false)));
		LookupHasher after = new LookupHasher(properties(new Security(JWT, KEY_2, 2, "1:" + KEY_1, KEY_1, null, false)));
		LookupHasher retired = new LookupHasher(properties(new Security(JWT, KEY_2, 2, null, KEY_1, null, false)));

		String tag = before.hash("rfid-uid", "04A1B2C3D4E5F6");

		assertThat(after.hash("rfid-uid", "04A1B2C3D4E5F6")).isEqualTo(tag);
		assertThat(retired.hash("rfid-uid", "04A1B2C3D4E5F6")).isEqualTo(tag);
		assertThat(after.hash("receipt-link", "x")).isEqualTo(before.hash("receipt-link", "x"));
		// The current data key would give other hashes, which is why it is never the lookup key.
		assertThat(new LookupHasher(properties(new Security(JWT, KEY_2, null, null, null, null, false)))
			.hash("rfid-uid", "04A1B2C3D4E5F6")).isNotEqualTo(tag);
	}

	private static ZawedProperties properties(Security security) {
		return new ZawedProperties(Environment.LOCAL, new Cors(List.of("http://localhost:5173")), security);
	}

}
