package com.zawed.api.platform.crypto;

import java.nio.ByteBuffer;
import java.security.GeneralSecurityException;
import java.security.SecureRandom;

import javax.crypto.Cipher;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

import org.springframework.stereotype.Component;

import com.zawed.api.platform.config.ZawedProperties;

/**
 * Encrypts small secrets at rest (TOTP secrets; tag UIDs in B2) with AES-256-GCM.
 *
 * <p>Layout: {@code [key id: 1 byte][nonce: 12 bytes][ciphertext + tag: n + 16 bytes]}. The key id
 * is authenticated as associated data, so a value cannot be moved under a different key, and it lets
 * a second key be introduced for rotation: values written with key 1 stay readable while new values
 * use key 2. Associated data can also bind a value to its row (for example the user id), so a
 * ciphertext copied into another row does not decrypt.
 */
@Component
public class DataEncryptor {

	static final byte CURRENT_KEY_ID = 1;

	private static final int NONCE_BYTES = 12;
	private static final int TAG_BITS = 128;
	private static final String TRANSFORMATION = "AES/GCM/NoPadding";

	private final SecretKey key;
	private final SecureRandom random = new SecureRandom();

	@org.springframework.beans.factory.annotation.Autowired
	public DataEncryptor(ZawedProperties properties) {
		this(properties.security().dataKeyBytes());
	}

	DataEncryptor(byte[] keyBytes) {
		if (keyBytes.length != 32) {
			throw new IllegalArgumentException("AES-256 needs a 32-byte key");
		}
		this.key = new SecretKeySpec(keyBytes, "AES");
	}

	public byte[] encrypt(byte[] plaintext, byte[] associatedData) {
		byte[] nonce = new byte[NONCE_BYTES];
		random.nextBytes(nonce);
		try {
			Cipher cipher = Cipher.getInstance(TRANSFORMATION);
			cipher.init(Cipher.ENCRYPT_MODE, key, new GCMParameterSpec(TAG_BITS, nonce));
			cipher.updateAAD(new byte[] { CURRENT_KEY_ID });
			cipher.updateAAD(associatedData);
			byte[] sealed = cipher.doFinal(plaintext);
			return ByteBuffer.allocate(1 + NONCE_BYTES + sealed.length).put(CURRENT_KEY_ID).put(nonce).put(sealed).array();
		}
		catch (GeneralSecurityException ex) {
			throw new IllegalStateException("Encryption failed", ex);
		}
	}

	/** Fails when the value was changed, encrypted under another key, or bound to other associated data. */
	public byte[] decrypt(byte[] value, byte[] associatedData) {
		if (value == null || value.length < 1 + NONCE_BYTES + TAG_BITS / 8) {
			throw new IllegalArgumentException("Not an encrypted value");
		}
		if (value[0] != CURRENT_KEY_ID) {
			throw new IllegalArgumentException("Unknown key id " + value[0]);
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

}
