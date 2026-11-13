package com.zawed.api.platform.crypto;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.SecureRandom;
import java.util.Base64;
import java.util.HexFormat;

/**
 * Random tokens and their stored form.
 *
 * <p>A token handed to a client (refresh token, MFA challenge, temporary password) is random and
 * long, so it is stored as a plain SHA-256: there is nothing to brute-force, and a database leak
 * reveals no usable token. Passwords, which people choose, use Argon2 instead.
 */
public final class SecureTokens {

	private static final SecureRandom RANDOM = new SecureRandom();
	private static final Base64.Encoder URL_SAFE = Base64.getUrlEncoder().withoutPadding();

	/** Readable temporary passwords: no 0/O, 1/l/I. */
	private static final char[] PASSWORD_ALPHABET = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"
		.toCharArray();

	private SecureTokens() {
	}

	/** {@code bytes} random bytes, URL-safe base64 without padding. 32 bytes gives 256 bits. */
	public static String random(int bytes) {
		byte[] value = new byte[bytes];
		RANDOM.nextBytes(value);
		return URL_SAFE.encodeToString(value);
	}

	/** A temporary password of {@code length} characters (about 5.8 bits each). */
	public static String temporaryPassword(int length) {
		StringBuilder password = new StringBuilder(length);
		for (int i = 0; i < length; i++) {
			password.append(PASSWORD_ALPHABET[RANDOM.nextInt(PASSWORD_ALPHABET.length)]);
		}
		return password.toString();
	}

	public static String sha256Hex(String value) {
		try {
			MessageDigest digest = MessageDigest.getInstance("SHA-256");
			return HexFormat.of().formatHex(digest.digest(value.getBytes(StandardCharsets.UTF_8)));
		}
		catch (NoSuchAlgorithmException ex) {
			throw new IllegalStateException(ex);
		}
	}

	/** Compares secrets without leaking, through timing, how many leading characters match. */
	public static boolean constantTimeEquals(String a, String b) {
		if (a == null || b == null) {
			return false;
		}
		return MessageDigest.isEqual(a.getBytes(StandardCharsets.UTF_8), b.getBytes(StandardCharsets.UTF_8));
	}

}
