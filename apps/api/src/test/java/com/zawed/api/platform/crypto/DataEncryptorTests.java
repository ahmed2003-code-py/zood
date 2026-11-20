package com.zawed.api.platform.crypto;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Base64;
import java.util.HexFormat;
import java.util.Map;

import org.junit.jupiter.api.Test;

class DataEncryptorTests {

	private final DataEncryptor encryptor = new DataEncryptor(new byte[32]);
	private final byte[] secret = "a totp secret".getBytes(StandardCharsets.UTF_8);
	private final byte[] owner = "user-1".getBytes(StandardCharsets.UTF_8);

	@Test
	void roundTripsAndNeverRepeatsACiphertext() {
		byte[] first = encryptor.encrypt(secret, owner);
		byte[] second = encryptor.encrypt(secret, owner);

		assertThat(encryptor.decrypt(first, owner)).isEqualTo(secret);
		assertThat(first).isNotEqualTo(second); // a fresh nonce every time
		assertThat(DataEncryptor.keyIdOf(first)).isEqualTo(DataEncryptor.LEGACY_KEY_ID);
	}

	@Test
	void detectsAnyChange() {
		byte[] value = encryptor.encrypt(secret, owner);
		byte[] tampered = Arrays.copyOf(value, value.length);
		tampered[tampered.length - 1] ^= 1;

		assertThatThrownBy(() -> encryptor.decrypt(tampered, owner)).isInstanceOf(IllegalArgumentException.class);
	}

	@Test
	void aValueCopiedToAnotherOwnerDoesNotDecrypt() {
		byte[] value = encryptor.encrypt(secret, owner);

		assertThatThrownBy(() -> encryptor.decrypt(value, "user-2".getBytes(StandardCharsets.UTF_8)))
			.isInstanceOf(IllegalArgumentException.class);
	}

	@Test
	void aDifferentKeyCannotRead() {
		byte[] value = encryptor.encrypt(secret, owner);
		byte[] otherKey = new byte[32];
		otherKey[0] = 1;

		assertThatThrownBy(() -> new DataEncryptor(otherKey).decrypt(value, owner))
			.isInstanceOf(IllegalArgumentException.class);
	}

	// ------------------------------------------------------------------------- rotation (ADR 0027)

	/** Key 1 of every existing deployment's development configuration (application.yaml). */
	private static final byte[] KEY_1 = Base64.getDecoder().decode("bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=");

	private static final byte[] KEY_2 = "new-data-key-for-rotation-tests!".getBytes(StandardCharsets.US_ASCII);

	/**
	 * Written by DataEncryptor as it was before rotation existed (commit f3b5827), under KEY_1, for a
	 * tag's binding. It must stay readable for ever: every value in the database looks like this.
	 */
	private static final String LEGACY_VALUE = "0172b69dfd9c98b05cb14d5cd718513a050c324c880fe45ac0234a6d5c410ec1ba347eba25a94c"
			+ "6cde62c922bbf4ec8946562572";

	private static final byte[] LEGACY_BINDING = "rfid_tags.uid:0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"
		.getBytes(StandardCharsets.US_ASCII);

	private static final byte[] LEGACY_PLAINTEXT = "JBSWY3DPEHPK3PXP-legacy".getBytes(StandardCharsets.UTF_8);

	@Test
	void aValueWrittenBeforeRotationExistedStillDecrypts() {
		byte[] legacy = HexFormat.of().parseHex(LEGACY_VALUE);

		assertThat(new DataEncryptor(KEY_1).decrypt(legacy, LEGACY_BINDING)).isEqualTo(LEGACY_PLAINTEXT);
		assertThat(rotated().decrypt(legacy, LEGACY_BINDING)).isEqualTo(LEGACY_PLAINTEXT);
		// The associated data is still the key id byte followed by the binding: any other binding fails.
		assertThatThrownBy(() -> rotated().decrypt(legacy, "rfid_tags.uid:other".getBytes(StandardCharsets.US_ASCII)))
			.isInstanceOf(IllegalArgumentException.class);
	}

	@Test
	void afterRotationNewValuesUseTheNewKeyAndOldValuesStillRead() {
		byte[] old = new DataEncryptor(KEY_1).encrypt(secret, owner);
		DataEncryptor rotated = rotated();

		byte[] fresh = rotated.encrypt(secret, owner);

		assertThat(DataEncryptor.keyIdOf(fresh)).isEqualTo(2);
		assertThat(rotated.currentKeyId()).isEqualTo(2);
		assertThat(rotated.decrypt(old, owner)).isEqualTo(secret);
		assertThat(rotated.decrypt(fresh, owner)).isEqualTo(secret);
		// The old deployment cannot read what the new one wrote: hence the staged first deploy (runbook).
		assertThatThrownBy(() -> new DataEncryptor(KEY_1).decrypt(fresh, owner)).hasMessageContaining("Unknown key id 2");
	}

	@Test
	void aValueUnderAKeyThisInstanceDoesNotHoldFailsLoudlyWithoutTryingAnotherKey() {
		byte[] old = new DataEncryptor(KEY_1).encrypt(secret, owner);
		DataEncryptor retired = new DataEncryptor(2, Map.of(2, KEY_2));

		assertThatThrownBy(() -> retired.decrypt(old, owner)).isInstanceOf(IllegalArgumentException.class)
			.hasMessageContaining("Unknown key id 1");
		assertThat(retired.knowsKey(1)).isFalse();
		byte[] unknown = Arrays.copyOf(old, old.length);
		unknown[0] = 9;
		assertThatThrownBy(() -> rotated().decrypt(unknown, owner)).hasMessageContaining("Unknown key id 9");
	}

	@Test
	void theKeyIdIsAuthenticatedSoAValueCannotBeRelabelled() {
		byte[] old = new DataEncryptor(KEY_1).encrypt(secret, owner);
		byte[] relabelled = Arrays.copyOf(old, old.length);
		relabelled[0] = 2;

		assertThatThrownBy(() -> rotated().decrypt(relabelled, owner)).hasMessageContaining("could not be verified");
	}

	@Test
	void reencryptionMovesAValueToTheCurrentKeyWithTheSameBindingAndLength() {
		byte[] old = new DataEncryptor(KEY_1).encrypt(secret, owner);

		byte[] moved = rotated().reencrypt(old, owner);

		assertThat(DataEncryptor.keyIdOf(moved)).isEqualTo(2);
		assertThat(moved).hasSameSizeAs(old); // what the V20 guards require of a re-encryption
		assertThat(new DataEncryptor(2, Map.of(2, KEY_2)).decrypt(moved, owner)).isEqualTo(secret);
		assertThatThrownBy(() -> rotated().reencrypt(old, "user-2".getBytes(StandardCharsets.UTF_8)))
			.isInstanceOf(IllegalArgumentException.class);
	}

	@Test
	void keyIdsAboveOneHundredAndTwentySevenAreReadAsUnsigned() {
		DataEncryptor high = new DataEncryptor(200, Map.of(200, KEY_2, 1, KEY_1));

		byte[] value = high.encrypt(secret, owner);

		assertThat(DataEncryptor.keyIdOf(value)).isEqualTo(200);
		assertThat(high.decrypt(value, owner)).isEqualTo(secret);
	}

	@Test
	void refusesAKeyRingItCannotUse() {
		assertThatThrownBy(() -> new DataEncryptor(2, Map.of(1, KEY_1))).hasMessageContaining("not in the key ring");
		assertThatThrownBy(() -> new DataEncryptor(1, Map.of(1, new byte[16]))).hasMessageContaining("32-byte");
		assertThatThrownBy(() -> new DataEncryptor(0, Map.of(0, KEY_1))).hasMessageContaining("1 to 255");
		assertThatThrownBy(() -> new DataEncryptor(256, Map.of(256, KEY_1))).hasMessageContaining("1 to 255");
		assertThatThrownBy(() -> DataEncryptor.keyIdOf(new byte[5])).hasMessageContaining("Not an encrypted value");
	}

	/** During a rotation: key 2 is current, key 1 is kept to read. */
	private static DataEncryptor rotated() {
		return new DataEncryptor(2, Map.of(2, KEY_2, 1, KEY_1));
	}

}
