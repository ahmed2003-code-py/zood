package com.zawed.api.platform.config;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Base64;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

import jakarta.validation.Valid;
import jakarta.validation.constraints.AssertTrue;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotEmpty;
import jakarta.validation.constraints.NotNull;

import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.validation.annotation.Validated;

/**
 * ZAWED's own configuration, checked once at boot. A deploy with unsafe values refuses to start
 * instead of running quietly misconfigured.
 */
@ConfigurationProperties("zawed")
@Validated
public record ZawedProperties(@NotNull Environment environment, @NotNull @Valid Cors cors,
		@NotNull @Valid Security security) {

	/** HS256 needs a key at least as long as its output. */
	static final int MIN_JWT_SECRET_BYTES = 32;

	/** AES-256. */
	static final int DATA_KEY_BYTES = 32;

	/**
	 * A data key's id is the first byte of every value it encrypts (DataEncryptor), so it is 1 to 255.
	 * A deployment that sets only ZAWED_DATA_KEY has key 1, which is also what every value written
	 * before key rotation existed carries (ADR 0027).
	 */
	public static final int DEFAULT_DATA_KEY_ID = 1;

	static final int MAX_DATA_KEY_ID = 255;

	/** A bootstrap token is a one-time password for the first Super Admin; it must not be guessable. */
	static final int MIN_BOOTSTRAP_TOKEN_CHARS = 32;

	/**
	 * The development values committed in application.yaml. They are public, so a deployed instance
	 * that carries any of them refuses to start.
	 */
	static final String COMMITTED_LOCAL_DATA_KEY = "bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=";

	static final Set<String> COMMITTED_LOCAL_VALUES = Set.of("local-only-jwt-secret-do-not-deploy-0123456789abcdef",
			COMMITTED_LOCAL_DATA_KEY, "local-only-bootstrap-token-do-not-deploy");

	public enum Environment {

		LOCAL, TEST, DEV, STAGING, PRODUCTION;

		/** True for anything reachable from the internet. */
		public boolean isDeployed() {
			return this == DEV || this == STAGING || this == PRODUCTION;
		}

	}

	public record Cors(@NotEmpty List<@NotBlank String> allowedOrigins) {
	}

	/**
	 * @param jwtSecret signs access tokens (HS256), at least 32 bytes
	 * @param dataKey base64 of the current 32-byte AES key: new values are encrypted with it (TOTP
	 *     secrets, tag UIDs, bank account numbers)
	 * @param dataKeyId the current key's id, 1 to 255; 1 when not set (ADR 0027)
	 * @param dataKeysPrevious keys that only decrypt, during a rotation: {@code id:base64} entries
	 *     separated by commas, for example {@code 1:<base64>}; empty when no rotation is under way
	 * @param lookupKey base64 of the 32-byte key of the lookup hashes (tag UIDs, device activation
	 *     codes, receipt links, the offline tag digest key). It does not rotate with the data key: when
	 *     not set it is {@code dataKey}, which is allowed only while {@code dataKeyId} is 1. It is always
	 *     the original key 1: checked here while key 1 is in the ring, and against the fingerprint the
	 *     database recorded at the first start (platform.crypto.DataKeyState)
	 * @param bootstrapToken optional; enables the one-time creation of the first Super Admin
	 * @param cookieSecure whether session cookies carry the Secure attribute; always true when deployed
	 */
	public record Security(@NotBlank String jwtSecret, @NotBlank String dataKey, Integer dataKeyId,
			String dataKeysPrevious, String lookupKey, String bootstrapToken, boolean cookieSecure) {

		public byte[] jwtSecretBytes() {
			return jwtSecret.getBytes(StandardCharsets.UTF_8);
		}

		public byte[] dataKeyBytes() {
			return Base64.getDecoder().decode(dataKey);
		}

		public int currentDataKeyId() {
			return dataKeyId == null ? DEFAULT_DATA_KEY_ID : dataKeyId;
		}

		/**
		 * The previous keys in the order they are listed, duplicates included (the checks below refuse
		 * them). @throws IllegalArgumentException when an entry is not {@code id:base64} of 32 bytes
		 * with an id from 1 to 255
		 */
		public List<Map.Entry<Integer, byte[]>> previousDataKeys() {
			List<Map.Entry<Integer, byte[]>> keys = new ArrayList<>();
			if (dataKeysPrevious == null || dataKeysPrevious.isBlank()) {
				return keys;
			}
			for (String entry : dataKeysPrevious.split(",", -1)) {
				String trimmed = entry.strip();
				int colon = trimmed.indexOf(':');
				if (colon <= 0) {
					throw new IllegalArgumentException("A previous data key is not written as id:base64");
				}
				int id = Integer.parseInt(trimmed.substring(0, colon).strip());
				byte[] key = Base64.getDecoder().decode(trimmed.substring(colon + 1).strip());
				if (id < 1 || id > MAX_DATA_KEY_ID || key.length != DATA_KEY_BYTES) {
					throw new IllegalArgumentException("A previous data key has a bad id or length");
				}
				keys.add(Map.entry(id, key));
			}
			return keys;
		}

		/**
		 * Every data key this instance can decrypt with, by id: the current one, then the previous ones.
		 * Meaningful once the configuration passed its checks, which happens before any bean uses it.
		 */
		public Map<Integer, byte[]> dataKeys() {
			Map<Integer, byte[]> keys = new LinkedHashMap<>();
			keys.put(currentDataKeyId(), dataKeyBytes());
			for (Map.Entry<Integer, byte[]> previous : previousDataKeys()) {
				keys.putIfAbsent(previous.getKey(), previous.getValue());
			}
			return keys;
		}

		public boolean hasLookupKey() {
			return lookupKey != null && !lookupKey.isBlank();
		}

		/** The key of the lookup hashes: its own value when set, otherwise the data key (key 1). */
		public byte[] lookupKeyBytes() {
			return hasLookupKey() ? Base64.getDecoder().decode(lookupKey.strip()) : dataKeyBytes();
		}

		public boolean bootstrapEnabled() {
			return bootstrapToken != null && !bootstrapToken.isBlank();
		}

		@Override
		public String toString() {
			return "Security[secrets hidden, dataKeyId=" + currentDataKeyId() + ", cookieSecure=" + cookieSecure + "]";
		}

	}

	/**
	 * Browsers send credentials to the origins listed here, so the list is always explicit, and a
	 * deployed environment accepts only https origins that are not this machine.
	 */
	@AssertTrue(message = "zawed.cors.allowed-origins must list explicit origins; a deployed environment accepts only "
			+ "https origins and never localhost")
	public boolean isCorsSafe() {
		if (environment == null || cors == null || cors.allowedOrigins() == null) {
			return true; // reported by the constraints on those fields
		}
		for (String origin : cors.allowedOrigins()) {
			if (origin == null || origin.contains("*")) {
				return false;
			}
			if (environment.isDeployed() && (!origin.startsWith("https://") || isLoopback(origin))) {
				return false;
			}
		}
		return true;
	}

	@AssertTrue(message = "zawed.security.jwt-secret must be at least 32 bytes")
	public boolean isJwtSecretStrong() {
		return security == null || security.jwtSecret() == null
				|| security.jwtSecretBytes().length >= MIN_JWT_SECRET_BYTES;
	}

	@AssertTrue(message = "zawed.security.data-key must be the base64 of exactly 32 random bytes")
	public boolean isDataKeyValid() {
		if (security == null || security.dataKey() == null) {
			return true;
		}
		try {
			return security.dataKeyBytes().length == DATA_KEY_BYTES;
		}
		catch (IllegalArgumentException ex) {
			return false;
		}
	}

	@AssertTrue(message = "zawed.security.bootstrap-token must be at least 32 characters when set")
	public boolean isBootstrapTokenStrong() {
		return security == null || !security.bootstrapEnabled()
				|| security.bootstrapToken().length() >= MIN_BOOTSTRAP_TOKEN_CHARS;
	}

	@AssertTrue(message = "zawed.security.data-key-id (ZAWED_DATA_KEY_ID) must be a key id from 1 to 255")
	public boolean isDataKeyIdValid() {
		return security == null || security.dataKeyId() == null
				|| (security.dataKeyId() >= 1 && security.dataKeyId() <= MAX_DATA_KEY_ID);
	}

	@AssertTrue(message = "zawed.security.data-keys-previous (ZAWED_DATA_KEYS_PREVIOUS) must be comma-separated "
			+ "id:base64 entries, each id from 1 to 255 and each key the base64 of exactly 32 bytes")
	public boolean isPreviousDataKeysValid() {
		if (security == null) {
			return true;
		}
		try {
			security.previousDataKeys();
			return true;
		}
		catch (IllegalArgumentException ex) {
			return false;
		}
	}

	@AssertTrue(message = "zawed.security.data-keys-previous (ZAWED_DATA_KEYS_PREVIOUS) lists the same key id twice")
	public boolean isPreviousDataKeyIdsUnique() {
		List<Map.Entry<Integer, byte[]>> previous = previousOrEmpty();
		return previous.stream().map(Map.Entry::getKey).distinct().count() == previous.size();
	}

	@AssertTrue(message = "zawed.security.data-key-id (ZAWED_DATA_KEY_ID) is also listed in data-keys-previous "
			+ "(ZAWED_DATA_KEYS_PREVIOUS): the current key is never a previous one")
	public boolean isDataKeyIdNotAlsoPrevious() {
		if (security == null) {
			return true;
		}
		int current = security.currentDataKeyId();
		return previousOrEmpty().stream().noneMatch((entry) -> entry.getKey() == current);
	}

	/**
	 * Catches a rotation that forgot to generate a new key: the same bytes under two ids would pass
	 * every other check, and the "rotation" would change nothing.
	 */
	@AssertTrue(message = "the same data key appears under two ids (ZAWED_DATA_KEY and ZAWED_DATA_KEYS_PREVIOUS): "
			+ "a rotation needs a newly generated key")
	public boolean isEveryDataKeyDifferent() {
		if (security == null || security.dataKey() == null || !isDataKeyValid()) {
			return true;
		}
		List<byte[]> keys = new ArrayList<>();
		keys.add(security.dataKeyBytes());
		previousOrEmpty().forEach((entry) -> keys.add(entry.getValue()));
		Set<String> distinct = new HashSet<>();
		keys.forEach((key) -> distinct.add(Arrays.toString(key)));
		return distinct.size() == keys.size();
	}

	@AssertTrue(message = "zawed.security.lookup-key (ZAWED_LOOKUP_KEY) must be the base64 of exactly 32 bytes when set")
	public boolean isLookupKeyValid() {
		if (security == null || !security.hasLookupKey()) {
			return true;
		}
		try {
			return security.lookupKeyBytes().length == DATA_KEY_BYTES;
		}
		catch (IllegalArgumentException ex) {
			return false;
		}
	}

	/**
	 * The lookup hashes stored in the database (tag UIDs, activation codes), and the receipt links and
	 * tag digest keys already handed out, were made with the original data key. Once the current data
	 * key is another one, that original key must be named explicitly, or every tag lookup would
	 * silently miss (ADR 0027).
	 */
	@AssertTrue(message = "zawed.security.lookup-key (ZAWED_LOOKUP_KEY) must be set once ZAWED_DATA_KEY_ID is not 1, "
			+ "to the key the lookup hashes were made with (the original ZAWED_DATA_KEY), never to the current data key")
	public boolean isLookupKeyPinnedAfterRotation() {
		if (security == null || security.currentDataKeyId() == DEFAULT_DATA_KEY_ID) {
			return true;
		}
		if (!security.hasLookupKey()) {
			return false;
		}
		if (!isLookupKeyValid() || security.dataKey() == null || !isDataKeyValid()) {
			return true; // reported by those checks
		}
		return !Arrays.equals(security.lookupKeyBytes(), security.dataKeyBytes());
	}

	/**
	 * The lookup key is the original data key, which is key 1. While key 1 is still in the ring (before a
	 * rotation, or until its previous key is removed), a lookup key that is anything else is a mistake,
	 * typically a freshly generated value: it would match none of the stored hashes. After key 1 is
	 * retired, the fingerprint recorded in the database at the first start catches the same mistake
	 * (platform.crypto.DataKeyState).
	 */
	@AssertTrue(message = "zawed.security.lookup-key (ZAWED_LOOKUP_KEY) must be the original data key: while key 1 is "
			+ "in the ring (ZAWED_DATA_KEY with id 1, or 1: in ZAWED_DATA_KEYS_PREVIOUS) it must be exactly key 1")
	public boolean isLookupKeyTheOriginalDataKey() {
		if (security == null || !security.hasLookupKey() || !isLookupKeyValid() || security.dataKey() == null
				|| !isDataKeyValid()) {
			return true; // reported by those checks
		}
		byte[] keyOne = security.currentDataKeyId() == DEFAULT_DATA_KEY_ID ? security.dataKeyBytes()
				: previousOrEmpty().stream()
					.filter((entry) -> entry.getKey() == DEFAULT_DATA_KEY_ID)
					.map(Map.Entry::getValue)
					.findFirst()
					.orElse(null);
		return keyOne == null || Arrays.equals(security.lookupKeyBytes(), keyOne);
	}

	@AssertTrue(message = "a deployed environment must set its own ZAWED_JWT_SECRET, ZAWED_DATA_KEY and "
			+ "ZAWED_BOOTSTRAP_TOKEN (the committed development values are refused, also as a previous data key or "
			+ "as ZAWED_LOOKUP_KEY), and send cookies Secure")
	public boolean isDeployedSecurityOwn() {
		if (environment == null || security == null || !environment.isDeployed()) {
			return true;
		}
		return security.cookieSecure() && !COMMITTED_LOCAL_VALUES.contains(security.jwtSecret())
				&& !COMMITTED_LOCAL_VALUES.contains(security.dataKey())
				&& (!security.bootstrapEnabled() || !COMMITTED_LOCAL_VALUES.contains(security.bootstrapToken()))
				&& previousOrEmpty().stream().noneMatch((entry) -> isCommittedDataKey(entry.getValue()))
				&& (!security.hasLookupKey() || !isLookupKeyValid() || !isCommittedDataKey(security.lookupKeyBytes()));
	}

	private List<Map.Entry<Integer, byte[]>> previousOrEmpty() {
		if (security == null) {
			return List.of();
		}
		try {
			return security.previousDataKeys();
		}
		catch (IllegalArgumentException ex) {
			return List.of(); // reported by isPreviousDataKeysValid
		}
	}

	/** Compared as bytes: the same key can be written in base64 with or without its padding. */
	private static boolean isCommittedDataKey(byte[] key) {
		return Arrays.equals(key, Base64.getDecoder().decode(COMMITTED_LOCAL_DATA_KEY));
	}

	private static boolean isLoopback(String origin) {
		return origin.contains("://localhost") || origin.contains("://127.") || origin.contains("://[::1]");
	}

}
