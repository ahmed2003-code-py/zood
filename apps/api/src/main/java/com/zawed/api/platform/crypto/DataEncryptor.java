package com.zawed.api.platform.crypto;

import java.nio.ByteBuffer;
import java.security.GeneralSecurityException;
import java.security.SecureRandom;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.Map;

import javax.crypto.Cipher;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.stereotype.Component;

import com.zawed.api.platform.config.ZawedProperties;

/**
 * Encrypts small secrets at rest (TOTP secrets, tag UIDs, bank account numbers) with AES-256-GCM.
 *
 * <p>Layout: {@code [key id: 1 byte][nonce: 12 bytes][ciphertext + tag: n + 16 bytes]}. The key id
 * is authenticated as associated data, so a value cannot be moved under a different key. Associated
 * data can also bind a value to its row (for example the user id), so a ciphertext copied into
 * another row does not decrypt.
 *
 * <p>Key rotation (ADR 0027): values are always written with the current key, and read with the key
 * their first byte names, which is the current key or one of the previous keys still configured.
 * A value under any other id is refused loudly; there is no fallback to another key. The background
 * job {@link DataKeyRotation} moves old values to the current key.
 */
@Component
public class DataEncryptor {

	/** The id of a deployment that sets only ZAWED_DATA_KEY, and of every value written before rotation. */
	static final int LEGACY_KEY_ID = ZawedProperties.DEFAULT_DATA_KEY_ID;

	private static final int MAX_KEY_ID = 255;
	private static final int NONCE_BYTES = 12;
	private static final int TAG_BITS = 128;
	private static final int SHORTEST_VALUE = 1 + NONCE_BYTES + TAG_BITS / 8;
	private static final String TRANSFORMATION = "AES/GCM/NoPadding";

	private final int currentKeyId;
	private final Map<Integer, SecretKey> keys;
	private final SecureRandom random = new SecureRandom();

	@Autowired
	public DataEncryptor(ZawedProperties properties) {
		this(properties.security().currentDataKeyId(), properties.security().dataKeys());
	}

	/** One key, with id 1: the configuration of a deployment that never rotated. */
	DataEncryptor(byte[] keyBytes) {
		this(LEGACY_KEY_ID, Map.of(LEGACY_KEY_ID, keyBytes));
	}

	/**
	 * @param currentKeyId the key new values are written with; it must be in {@code keyBytesById}
	 * @param keyBytesById every key this instance may read with, by id
	 */
	DataEncryptor(int currentKeyId, Map<Integer, byte[]> keyBytesById) {
		Map<Integer, SecretKey> ring = new LinkedHashMap<>();
		keyBytesById.forEach((id, bytes) -> {
			if (id < 1 || id > MAX_KEY_ID) {
				throw new IllegalArgumentException("A data key id is 1 to 255");
			}
			if (bytes.length != 32) {
				throw new IllegalArgumentException("AES-256 needs a 32-byte key");
			}
			ring.put(id, new SecretKeySpec(bytes, "AES"));
		});
		if (!ring.containsKey(currentKeyId)) {
			throw new IllegalArgumentException("The current data key is not in the key ring");
		}
		this.currentKeyId = currentKeyId;
		this.keys = Map.copyOf(ring);
	}

	/** The id new values are written with. */
	public int currentKeyId() {
		return currentKeyId;
	}

	/** Whether this instance holds the key with this id. */
	public boolean knowsKey(int keyId) {
		return keys.containsKey(keyId);
	}

	/** The key id a value was written with (its first byte, 1 to 255). */
	public static int keyIdOf(byte[] value) {
		if (value == null || value.length < SHORTEST_VALUE) {
			throw new IllegalArgumentException("Not an encrypted value");
		}
		return Byte.toUnsignedInt(value[0]);
	}

	public byte[] encrypt(byte[] plaintext, byte[] associatedData) {
		byte[] nonce = new byte[NONCE_BYTES];
		random.nextBytes(nonce);
		byte keyId = (byte) currentKeyId;
		try {
			Cipher cipher = Cipher.getInstance(TRANSFORMATION);
			cipher.init(Cipher.ENCRYPT_MODE, keys.get(currentKeyId), new GCMParameterSpec(TAG_BITS, nonce));
			// Unchanged since key 1: the key id byte, then the caller's binding, as associated data.
			cipher.updateAAD(new byte[] { keyId });
			cipher.updateAAD(associatedData);
			byte[] sealed = cipher.doFinal(plaintext);
			return ByteBuffer.allocate(1 + NONCE_BYTES + sealed.length).put(keyId).put(nonce).put(sealed).array();
		}
		catch (GeneralSecurityException ex) {
			throw new IllegalStateException("Encryption failed", ex);
		}
	}

	/**
	 * Fails when the value was changed, is bound to other associated data, or names a key this
	 * instance does not hold (never tried with another key instead).
	 */
	public byte[] decrypt(byte[] value, byte[] associatedData) {
		int keyId = keyIdOf(value);
		SecretKey key = keys.get(keyId);
		if (key == null) {
			throw new IllegalArgumentException("Unknown key id " + keyId + ": this instance does not hold that data key");
		}
		try {
			Cipher cipher = Cipher.getInstance(TRANSFORMATION);
			cipher.init(Cipher.DECRYPT_MODE, key, new GCMParameterSpec(TAG_BITS, value, 1, NONCE_BYTES));
			cipher.updateAAD(new byte[] { value[0] });
			cipher.updateAAD(associatedData);
			return cipher.doFinal(value, 1 + NONCE_BYTES, value.length - 1 - NONCE_BYTES);
		}
		catch (GeneralSecurityException ex) {
			throw new IllegalArgumentException("The encrypted value could not be verified", ex);
		}
	}

	/**
	 * The same plaintext under the current key, bound to the same associated data. The result has the
	 * same length as the value (GCM does not pad), which the database guards of V20 check.
	 */
	public byte[] reencrypt(byte[] value, byte[] associatedData) {
		byte[] plaintext = decrypt(value, associatedData);
		try {
			return encrypt(plaintext, associatedData);
		}
		finally {
			Arrays.fill(plaintext, (byte) 0);
		}
	}

}
