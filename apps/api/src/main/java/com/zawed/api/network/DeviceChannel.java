package com.zawed.api.network;

import static com.zawed.api.platform.persistence.Sql.ts;

import java.math.BigInteger;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.KeyFactory;
import java.security.PublicKey;
import java.security.Signature;
import java.security.interfaces.ECPublicKey;
import java.security.spec.X509EncodedKeySpec;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.Base64;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;
import java.util.function.Supplier;

import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.platform.crypto.LookupHasher;
import com.zawed.api.platform.crypto.SecureTokens;
import com.zawed.api.platform.ratelimit.RateLimiter;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.web.ApiException;

/**
 * The device channel (ADR 0010): a POS redeems its one-time activation code and registers its own
 * public key; from then on it gets short-lived device tokens by signing a timestamp and a nonce with
 * the private key, which never leaves the device. No shared secret ships in the app.
 *
 * <p>A device has no user and so no scope. Every database step here runs under an explicit platform
 * scope ({@link #asPlatform}), entered only for the device's own row and only after the step's
 * proof (the activation code, the signature) is what is being checked. This is the one escalation
 * the device channel uses; it is named, documented in the B4 contract and tested.
 */
@Service
class DeviceChannel {

	/** A device's answer to "who are you" on each request. */
	record DeviceRef(UUID deviceId, String code, UUID stationId, UUID operatorId) {
	}

	record Activated(DeviceRef device) {
	}

	static final int MAX_ACTIVATION_ATTEMPTS = 5;
	/** How far a token request's timestamp may be from server time. */
	static final Duration CLOCK_WINDOW = Duration.ofMinutes(5);
	static final String KEY_ALGORITHM = "EC_P256";
	private static final int ACTIVATIONS_PER_MINUTE = 10;
	private static final int TOKENS_PER_MINUTE = 20;

	/** The order of the P-256 group: a key on any other curve is refused. */
	private static final BigInteger P256_ORDER = new BigInteger(
			"FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", 16);

	private final JdbcClient jdbc;
	private final LookupHasher lookup;
	private final DeviceTokens tokens;
	private final AuditLog audit;
	private final RateLimiter limiter;
	private final TransactionTemplate tx;
	private final Clock clock;

	DeviceChannel(JdbcClient jdbc, LookupHasher lookup, DeviceTokens tokens, AuditLog audit, RateLimiter limiter,
			TransactionTemplate tx, Clock clock) {
		this.jdbc = jdbc;
		this.lookup = lookup;
		this.tokens = tokens;
		this.audit = audit;
		this.limiter = limiter;
		this.tx = tx;
		this.clock = clock;
	}

	// ------------------------------------------------------------------------------------ activate

	/**
	 * Redeems an activation code: the device becomes ACTIVE with the given public key, and the code
	 * can never be used again. A wrong code counts an attempt; after {@value #MAX_ACTIVATION_ATTEMPTS}
	 * the code is dead and Ops must issue a new one. An unknown device code, a wrong, expired or used
	 * activation code and a revoked device all answer the same, so the endpoint reveals nothing.
	 */
	Activated activate(String deviceCode, String activationCode, String publicKeyBase64, String clientAddress) {
		limiter.acquire("pos-activate:" + SecureTokens.sha256Hex(String.valueOf(clientAddress)), ACTIVATIONS_PER_MINUTE,
				Duration.ofMinutes(1));
		byte[] publicKey = parseP256(publicKeyBase64);
		String code = deviceCode == null ? "" : deviceCode.strip().toUpperCase(java.util.Locale.ROOT);
		String hash = lookup.hash(Devices.ACTIVATION_NAMESPACE, activationCode == null ? "" : activationCode.strip().toUpperCase(java.util.Locale.ROOT));
		Instant now = clock.instant();
		Optional<Activated> activated = asPlatform(() -> {
			Optional<Map<String, Object>> row = jdbc.sql("""
					select id, code, station_id, operator_id, status, activation_code_hash, activation_expires_at,
					       activation_attempts
					from pos_devices where code = :code for update
					""").param("code", code).query().listOfRows().stream().findFirst();
			if (row.isEmpty()) {
				return Optional.<Activated>empty();
			}
			Map<String, Object> device = row.get();
			UUID id = (UUID) device.get("id");
			boolean usable = "PENDING_ACTIVATION".equals(device.get("status")) && device.get("activation_code_hash") != null
					&& device.get("activation_expires_at") != null
					&& ((java.sql.Timestamp) device.get("activation_expires_at")).toInstant().isAfter(now)
					&& ((Number) device.get("activation_attempts")).intValue() < MAX_ACTIVATION_ATTEMPTS;
			boolean matches = usable && SecureTokens.constantTimeEquals(hash, (String) device.get("activation_code_hash"));
			if (!matches) {
				if (usable) {
					jdbc.sql("update pos_devices set activation_attempts = activation_attempts + 1 where id = :id")
						.param("id", id)
						.update();
				}
				return Optional.<Activated>empty();
			}
			jdbc.sql("""
					update pos_devices set status = 'ACTIVE', public_key = :key, key_algorithm = :alg, bound_at = :now,
					       activation_code_hash = null, activation_expires_at = null, updated_at = :now
					where id = :id
					""")
				.param("id", id)
				.param("key", publicKey)
				.param("alg", KEY_ALGORITHM)
				.param("now", ts(now))
				.update();
			UUID operator = (UUID) device.get("operator_id");
			audit.record(new AuditLog.Actor(null, null), "DEVICE_ACTIVATED", "POS_DEVICE", id, Scope.operator(operator),
					Map.of("code", code, "keyAlgorithm", KEY_ALGORITHM));
			return Optional.of(new Activated(new DeviceRef(id, code, (UUID) device.get("station_id"), operator)));
		});
		return activated.orElseThrow(() -> new ApiException(HttpStatus.UNAUTHORIZED, "ACTIVATION_FAILED",
				"The device code or activation code is not valid"));
	}

	// --------------------------------------------------------------------------------------- token

	/** The message a device signs to get a token. Fixed layout, one field per line. */
	static String tokenMessage(UUID deviceId, long timestampSeconds, String nonce) {
		return "zawed-device-token\n" + deviceId + "\n" + timestampSeconds + "\n" + nonce;
	}

	/**
	 * A device token for a device that proves it holds its private key: an ECDSA (P-256, SHA-256,
	 * DER) signature over {@link #tokenMessage}, a timestamp within {@link #CLOCK_WINDOW} of server
	 * time, and a nonce never seen before for this device.
	 */
	DeviceTokens.Issued token(UUID deviceId, long timestampSeconds, String nonce, String signatureBase64) {
		limiter.acquire("pos-token:" + deviceId, TOKENS_PER_MINUTE, Duration.ofMinutes(1));
		Instant now = clock.instant();
		if (Math.abs(now.getEpochSecond() - timestampSeconds) > CLOCK_WINDOW.toSeconds()) {
			throw new ApiException(HttpStatus.UNAUTHORIZED, "DEVICE_CLOCK_SKEW",
					"The device clock is too far from server time", null,
					Map.of("serverTime", now.getEpochSecond()));
		}
		if (nonce == null || nonce.length() < 16 || nonce.length() > 128) {
			throw refused();
		}
		boolean ok = asPlatform(() -> {
			Optional<byte[]> key = jdbc.sql("select public_key from pos_devices where id = :id and status = 'ACTIVE'")
				.param("id", deviceId)
				.query(byte[].class)
				.optional();
			if (key.isEmpty() || !verify(key.get(), tokenMessage(deviceId, timestampSeconds, nonce), signatureBase64)) {
				return false;
			}
			try {
				jdbc.sql("insert into device_nonces (device_id, nonce, created_at) values (:device, :nonce, :now)")
					.param("device", deviceId)
					.param("nonce", nonce)
					.param("now", ts(now))
					.update();
			}
			catch (DuplicateKeyException ex) {
				throw new ApiException(HttpStatus.UNAUTHORIZED, "DEVICE_REPLAY", "This signed request was already used");
			}
			return true;
		});
		if (!ok) {
			throw refused();
		}
		return tokens.issue(deviceId);
	}

	// -------------------------------------------------------------------------------------- verify

	/** The device a token names, if it is still ACTIVE. Read on every request, so revocation is immediate. */
	Optional<DeviceRef> verify(String deviceToken) {
		return tokens.deviceOf(deviceToken)
			.flatMap((deviceId) -> asPlatform(() -> jdbc.sql("""
					select id, code, station_id, operator_id from pos_devices where id = :id and status = 'ACTIVE'
					""")
				.param("id", deviceId)
				.query((rs, n) -> new DeviceRef(rs.getObject("id", UUID.class), rs.getString("code"),
						rs.getObject("station_id", UUID.class), rs.getObject("operator_id", UUID.class)))
				.optional()));
	}

	/** Records the device's last contact, at most once a minute (the sync monitor's "last seen"). */
	void touch(UUID deviceId) {
		Instant now = clock.instant();
		asPlatform(() -> jdbc.sql("""
				update pos_devices set last_sync_at = :now
				where id = :id and (last_sync_at is null or last_sync_at < :stale)
				""")
			.param("id", deviceId)
			.param("now", ts(now))
			.param("stale", ts(now.minus(Duration.ofMinutes(1))))
			.update());
	}

	// ------------------------------------------------------------------------------------- helpers

	private <T> T asPlatform(Supplier<T> work) {
		return DbScope.runAs(new DbScope.Context(Scope.PLATFORM, null), () -> tx.execute((status) -> work.get()));
	}

	/** A P-256 public key as X.509 SubjectPublicKeyInfo, base64. Anything else is refused. */
	static byte[] parseP256(String publicKeyBase64) {
		try {
			byte[] encoded = Base64.getDecoder().decode(publicKeyBase64 == null ? "" : publicKeyBase64.strip());
			PublicKey key = KeyFactory.getInstance("EC").generatePublic(new X509EncodedKeySpec(encoded));
			if (!(key instanceof ECPublicKey ec) || !P256_ORDER.equals(ec.getParams().getOrder())) {
				throw invalidKey();
			}
			return key.getEncoded();
		}
		catch (GeneralSecurityException | IllegalArgumentException ex) {
			throw invalidKey();
		}
	}

	private static boolean verify(byte[] publicKey, String message, String signatureBase64) {
		try {
			PublicKey key = KeyFactory.getInstance("EC").generatePublic(new X509EncodedKeySpec(publicKey));
			Signature signature = Signature.getInstance("SHA256withECDSA");
			signature.initVerify(key);
			signature.update(message.getBytes(StandardCharsets.UTF_8));
			return signature.verify(Base64.getDecoder().decode(signatureBase64 == null ? "" : signatureBase64.strip()));
		}
		catch (GeneralSecurityException | IllegalArgumentException ex) {
			return false;
		}
	}

	private static ApiException invalidKey() {
		return new ApiException(HttpStatus.BAD_REQUEST, "INVALID_DEVICE_KEY",
				"The public key must be an EC P-256 key in X.509 SubjectPublicKeyInfo form, base64", "publicKey", null);
	}

	private static ApiException refused() {
		return new ApiException(HttpStatus.UNAUTHORIZED, "DEVICE_UNAUTHENTICATED", "The device could not be authenticated");
	}
}
