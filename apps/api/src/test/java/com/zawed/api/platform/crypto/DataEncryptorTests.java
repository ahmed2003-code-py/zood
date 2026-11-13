package com.zawed.api.platform.crypto;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;

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
		assertThat(first[0]).isEqualTo(DataEncryptor.CURRENT_KEY_ID);
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

}
