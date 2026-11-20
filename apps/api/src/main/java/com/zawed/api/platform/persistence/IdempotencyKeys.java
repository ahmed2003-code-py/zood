package com.zawed.api.platform.persistence;

import static com.zawed.api.platform.persistence.Sql.ts;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.time.Clock;
import java.util.HexFormat;
import java.util.Optional;
import java.util.UUID;

import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Component;
import org.springframework.transaction.annotation.Propagation;
import org.springframework.transaction.annotation.Transactional;

import tools.jackson.databind.ObjectMapper;

import com.zawed.api.platform.web.ApiException;

/**
 * Idempotency-Key for creates without a natural key (V13, ADR 0022).
 *
 * <p>Call {@link #claim} first, inside the transaction that creates the resource. Empty means "go
 * ahead and create {@code newId}"; a value is the resource an earlier request with the same key and
 * the same body created, which the caller returns as it is. The same key with a different body is
 * {@code 409 IDEMPOTENCY_KEY_REUSED}. Because the key row commits or rolls back with the resource, a
 * failed create never burns its key, and a concurrent request with the same key waits on the
 * primary key until the first one ends, then sees its result.
 *
 * <p>Where a duplicate would move money ({@code wallet_adjustment.create}), the key is mandatory:
 * call {@link #claimRequired}. Keys are purged after {@code zawed.idempotency.key-retention}
 * ({@link IdempotencyKeyRetention}, V17).
 */
@Component
public class IdempotencyKeys {

	/** The header clients send. */
	public static final String HEADER = "Idempotency-Key";

	/** A missing, blank, too short or too long key (the code the web client already translates). */
	public static final String INVALID_KEY = "INVALID_IDEMPOTENCY_KEY";

	private final JdbcClient jdbc;
	private final ObjectMapper json;
	private final Clock clock;

	IdempotencyKeys(JdbcClient jdbc, ObjectMapper json, Clock clock) {
		this.jdbc = jdbc;
		this.json = json;
		this.clock = clock;
	}

	/**
	 * @param key the header value; null or blank means the client asked for no idempotency
	 * @param request the request body, hashed in its JSON form so the same body always matches
	 * @return the resource created earlier under this key, or empty to create {@code newId} now
	 */
	@Transactional(propagation = Propagation.MANDATORY)
	public Optional<UUID> claim(UUID actorId, String operation, String key, Object request, UUID newId) {
		if (key == null || key.isBlank()) {
			return Optional.empty();
		}
		String trimmed = key.strip();
		if (trimmed.length() < 8 || trimmed.length() > 200) {
			throw new ApiException(HttpStatus.BAD_REQUEST, INVALID_KEY, "The Idempotency-Key must be 8 to 200 characters",
					HEADER, null);
		}
		String hash = hash(request);
		int inserted = jdbc.sql("""
				insert into idempotency_keys (actor_id, operation, key, request_hash, resource_id, created_at)
				values (:actor, :operation, :key, :hash, :resource, :now)
				on conflict do nothing
				""")
			.param("actor", actorId)
			.param("operation", operation)
			.param("key", trimmed)
			.param("hash", hash)
			.param("resource", newId)
			.param("now", ts(clock.instant()))
			.update();
		if (inserted == 1) {
			return Optional.empty();
		}
		Stored stored = jdbc.sql("""
				select request_hash, resource_id from idempotency_keys
				where actor_id = :actor and operation = :operation and key = :key
				""")
			.param("actor", actorId)
			.param("operation", operation)
			.param("key", trimmed)
			.query((rs, n) -> new Stored(rs.getString(1), rs.getObject(2, UUID.class)))
			.single();
		if (!stored.hash().equals(hash)) {
			throw new ApiException(HttpStatus.CONFLICT, "IDEMPOTENCY_KEY_REUSED",
					"This Idempotency-Key was already used for a different request", HEADER, null);
		}
		return Optional.of(stored.resourceId());
	}

	/**
	 * {@link #claim} for a create whose duplicate would move money: without a key a retry cannot be
	 * told from a second request, so none is accepted ({@code 400 INVALID_IDEMPOTENCY_KEY}, field
	 * {@value #HEADER}). Called from the service, after the endpoint's permission check, so a caller
	 * without the permission still gets 403 and learns nothing about the key.
	 */
	@Transactional(propagation = Propagation.MANDATORY)
	public Optional<UUID> claimRequired(UUID actorId, String operation, String key, Object request, UUID newId) {
		if (key == null || key.isBlank()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, INVALID_KEY,
					"This request must carry an Idempotency-Key header, so that a retry is never a second request",
					HEADER, null);
		}
		return claim(actorId, operation, key, request, newId);
	}

	private record Stored(String hash, UUID resourceId) {
	}

	private String hash(Object request) {
		try {
			byte[] body = json.writeValueAsString(request).getBytes(StandardCharsets.UTF_8);
			return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(body));
		}
		catch (NoSuchAlgorithmException ex) {
			throw new IllegalStateException(ex);
		}
	}

}
