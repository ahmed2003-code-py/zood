package com.zawed.api.identity;

import java.net.URLEncoder;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.OptionalLong;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

/**
 * Time-based one-time passwords, RFC 6238 with the values every authenticator app supports:
 * HMAC-SHA1, 6 digits, 30-second steps. Small enough to own outright, and tested against the RFC's
 * own vectors.
 */
final class Totp {

	static final int DIGITS = 6;
	static final long STEP_SECONDS = 30;
	/** One step either side absorbs clock drift between the phone and the server. */
	static final int WINDOW = 1;
	static final int SECRET_BYTES = 20;

	private static final String BASE32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
	private static final SecureRandom RANDOM = new SecureRandom();

	private Totp() {
	}

	static byte[] newSecret() {
		byte[] secret = new byte[SECRET_BYTES];
		RANDOM.nextBytes(secret);
		return secret;
	}

	static long stepAt(long epochSeconds) {
		return Math.floorDiv(epochSeconds, STEP_SECONDS);
	}

	/** The code for one time step, zero-padded to {@link #DIGITS}. */
	static String codeAt(byte[] secret, long step) {
		return codeAt(secret, step, DIGITS, "HmacSHA1");
	}

	static String codeAt(byte[] secret, long step, int digits, String algorithm) {
		try {
			Mac mac = Mac.getInstance(algorithm);
			mac.init(new SecretKeySpec(secret, algorithm));
			byte[] hash = mac.doFinal(ByteBuffer.allocate(8).putLong(step).array());
			int offset = hash[hash.length - 1] & 0x0f;
			int binary = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16)
					| ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);
			int otp = binary % (int) Math.pow(10, digits);
			return String.format("%0" + digits + "d", otp);
		}
		catch (GeneralSecurityException ex) {
			throw new IllegalStateException(ex);
		}
	}

	/**
	 * The step a code matches within the window around {@code epochSeconds}, or empty. Every step in
	 * the window is compared, in constant time, so timing does not reveal which one matched.
	 */
	static OptionalLong matchingStep(byte[] secret, String code, long epochSeconds) {
		if (code == null || code.length() != DIGITS || !code.chars().allMatch(Character::isDigit)) {
			return OptionalLong.empty();
		}
		long now = stepAt(epochSeconds);
		long matched = Long.MIN_VALUE;
		for (long step = now - WINDOW; step <= now + WINDOW; step++) {
			if (MessageDigest.isEqual(codeAt(secret, step).getBytes(StandardCharsets.US_ASCII),
					code.getBytes(StandardCharsets.US_ASCII))) {
				matched = step;
			}
		}
		return matched == Long.MIN_VALUE ? OptionalLong.empty() : OptionalLong.of(matched);
	}

	static String base32(byte[] bytes) {
		StringBuilder out = new StringBuilder((bytes.length * 8 + 4) / 5);
		int buffer = 0;
		int bits = 0;
		for (byte value : bytes) {
			buffer = (buffer << 8) | (value & 0xff);
			bits += 8;
			while (bits >= 5) {
				out.append(BASE32.charAt((buffer >> (bits - 5)) & 31));
				bits -= 5;
			}
		}
		if (bits > 0) {
			out.append(BASE32.charAt((buffer << (5 - bits)) & 31));
		}
		return out.toString();
	}

	/** The URI an authenticator app reads from the QR code. */
	static String otpauthUri(String issuer, String account, byte[] secret) {
		String label = URLEncoder.encode(issuer + ":" + account, StandardCharsets.UTF_8).replace("+", "%20");
		return "otpauth://totp/" + label + "?secret=" + base32(secret) + "&issuer="
				+ URLEncoder.encode(issuer, StandardCharsets.UTF_8) + "&algorithm=SHA1&digits=" + DIGITS + "&period="
				+ STEP_SECONDS;
	}

}
