package com.zawed.api.network;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;
import java.util.function.Supplier;

import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.network.DeviceChannel.DeviceRef;
import com.zawed.api.platform.crypto.PasswordHasher;
import com.zawed.api.platform.crypto.SecureTokens;
import com.zawed.api.platform.ratelimit.RateLimiter;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.web.ApiException;

/**
 * An agent signed in on a device (PRD 6.1): Worker ID and 6-digit PIN. A PIN has a million values,
 * so it is Argon2id-hashed, checked in constant time for unknown workers too, and locked after
 * {@value #MAX_FAILURES} wrong tries. The agent must work at the device's own station. One live
 * session per agent: signing in on another device ends the first (plan §6: no credential sharing).
 *
 * <p>Runs under the device channel's explicit platform scope, like {@link DeviceChannel}.
 */
@Service
class AgentSessions {

	/** The agent behind a request, verified against the session row on every call. */
	record AgentRef(UUID agentId, String workerId, String fullName, boolean mustChangePin) {
	}

	record SignedIn(String sessionToken, Instant expiresAt, AgentRef agent) {
	}

	static final int MAX_FAILURES = 5;
	static final Duration LOCK = Duration.ofMinutes(15);
	/** A shift, with margin. The agent signs in again for the next one. */
	static final Duration SESSION_LIFETIME = Duration.ofHours(12);
	private static final int SIGN_INS_PER_MINUTE = 10;

	private final JdbcClient jdbc;
	private final PasswordHasher hasher;
	private final AuditLog audit;
	private final RateLimiter limiter;
	private final TransactionTemplate tx;
	private final Clock clock;

	AgentSessions(JdbcClient jdbc, PasswordHasher hasher, AuditLog audit, RateLimiter limiter, TransactionTemplate tx,
			Clock clock) {
		this.jdbc = jdbc;
		this.hasher = hasher;
		this.audit = audit;
		this.limiter = limiter;
		this.tx = tx;
		this.clock = clock;
	}

	private record AgentRow(UUID id, UUID stationId, String workerId, String fullName, String pinHash,
			boolean mustChangePin, int failedPinCount, Instant lockedUntil, String status) {
	}

	private enum Outcome {

		OK, WRONG, LOCKED, DISABLED

	}

	SignedIn signIn(DeviceRef device, String workerId, String pin) {
		limiter.acquire("pos-agent:" + device.deviceId(), SIGN_INS_PER_MINUTE, Duration.ofMinutes(1));
		String worker = workerId == null ? "" : workerId.strip().toUpperCase(Locale.ROOT);
		String secret = pin == null ? "" : pin.strip();
		Instant now = clock.instant();
		record Attempt(Outcome outcome, SignedIn signedIn, Instant lockedUntil) {
		}
		// One transaction holding the agent row: failures are counted and committed (the outcome is
		// returned, not thrown), and a successful sign-in ends the old session and opens the new one
		// under the same lock, so two sign-ins at once cannot both succeed.
		Attempt attempt = asPlatform(() -> {
			Optional<AgentRow> row = jdbc.sql("select * from agents where worker_id = :worker for update")
				.param("worker", worker)
				.query((rs, n) -> new AgentRow(rs.getObject("id", UUID.class), rs.getObject("station_id", UUID.class),
						rs.getString("worker_id"), rs.getString("full_name"), rs.getString("pin_hash"),
						rs.getBoolean("must_change_pin"), rs.getInt("failed_pin_count"), instant(rs, "locked_until"),
						rs.getString("status")))
				.optional();
			if (row.isEmpty() || !row.get().stationId().equals(device.stationId())) {
				// Same work as a real check: the response time does not reveal whether a worker exists
				// or works elsewhere.
				hasher.burn(secret);
				return new Attempt(Outcome.WRONG, null, null);
			}
			AgentRow agent = row.get();
			if (agent.lockedUntil() != null && agent.lockedUntil().isAfter(now)) {
				hasher.burn(secret);
				return new Attempt(Outcome.LOCKED, null, agent.lockedUntil());
			}
			if (!hasher.matches(secret, agent.pinHash())) {
				int failures = agent.failedPinCount() + 1;
				Instant lockedUntil = failures >= MAX_FAILURES ? now.plus(LOCK) : null;
				jdbc.sql("update agents set failed_pin_count = :count, locked_until = :locked where id = :id")
					.param("count", lockedUntil == null ? failures : 0)
					.param("locked", ts(lockedUntil))
					.param("id", agent.id())
					.update();
				audit.record(new AuditLog.Actor(null, null), lockedUntil == null ? "AGENT_PIN_FAILED" : "AGENT_LOCKED",
						"AGENT", agent.id(), Scope.operator(device.operatorId()),
						Map.of("deviceId", device.deviceId().toString()));
				return new Attempt(lockedUntil == null ? Outcome.WRONG : Outcome.LOCKED, null, lockedUntil);
			}
			if (!"ACTIVE".equals(agent.status())) {
				return new Attempt(Outcome.DISABLED, null, null);
			}
			return new Attempt(Outcome.OK, open(device, agent, now), null);
		});

		return switch (attempt.outcome()) {
			case WRONG -> throw new ApiException(HttpStatus.UNAUTHORIZED, "AGENT_SIGN_IN_FAILED",
					"The worker ID or PIN is not correct");
			case LOCKED -> throw new ApiException(HttpStatus.LOCKED, "AGENT_LOCKED", "Too many wrong PINs", null,
					Map.of("retryAfterSeconds", Math.max(1, Duration.between(now, attempt.lockedUntil()).toSeconds())));
			case DISABLED -> throw new ApiException(HttpStatus.FORBIDDEN, "AGENT_DISABLED", "This agent cannot sign in");
			case OK -> attempt.signedIn();
		};
	}

	private SignedIn open(DeviceRef device, AgentRow agent, Instant now) {
		String token = SecureTokens.random(32);
		Instant expires = now.plus(SESSION_LIFETIME);
		jdbc.sql("update agents set failed_pin_count = 0, locked_until = null where id = :id")
			.param("id", agent.id())
			.update();
		int ended = jdbc.sql("""
				update agent_sessions set revoked_at = :now, revoke_reason = 'SIGNED_IN_ELSEWHERE'
				where agent_id = :agent and revoked_at is null
				""").param("agent", agent.id()).param("now", ts(now)).update();
		jdbc.sql("""
				insert into agent_sessions (id, agent_id, device_id, operator_id, station_id, token_hash, created_at,
				                            last_seen_at, expires_at)
				values (:id, :agent, :device, :operator, :station, :hash, :now, :now, :expires)
				""")
			.param("id", UUID.randomUUID())
			.param("agent", agent.id())
			.param("device", device.deviceId())
			.param("operator", device.operatorId())
			.param("station", device.stationId())
			.param("hash", SecureTokens.sha256Hex(token))
			.param("now", ts(now))
			.param("expires", ts(expires))
			.update();
		audit.record(new AuditLog.Actor(null, null), "AGENT_SIGNED_IN", "AGENT", agent.id(),
				Scope.operator(device.operatorId()),
				Map.of("deviceId", device.deviceId().toString(), "sessionsEnded", ended));
		return new SignedIn(token, expires,
				new AgentRef(agent.id(), agent.workerId(), agent.fullName(), agent.mustChangePin()));
	}

	/**
	 * The agent of a live session on this device: not revoked, not expired, the agent still ACTIVE
	 * and still at the device's station. Read on every request, so suspension is immediate.
	 */
	Optional<AgentRef> verify(DeviceRef device, String sessionToken) {
		if (sessionToken == null || sessionToken.isBlank()) {
			return Optional.empty();
		}
		Instant now = clock.instant();
		return asPlatform(() -> jdbc.sql("""
				select a.id, a.worker_id, a.full_name, a.must_change_pin
				from agent_sessions s join agents a on a.id = s.agent_id
				where s.token_hash = :hash and s.device_id = :device and s.revoked_at is null and s.expires_at > :now
				  and a.status = 'ACTIVE' and a.station_id = :station
				""")
			.param("hash", SecureTokens.sha256Hex(sessionToken))
			.param("device", device.deviceId())
			.param("station", device.stationId())
			.param("now", ts(now))
			.query((rs, n) -> new AgentRef(rs.getObject("id", UUID.class), rs.getString("worker_id"),
					rs.getString("full_name"), rs.getBoolean("must_change_pin")))
			.optional());
	}

	/** The agent replaces the PIN Ops gave them with their own (forced at first sign-in). */
	void changePin(DeviceRef device, AgentRef agent, String currentPin, String newPin) {
		String next = newPin == null ? "" : newPin.strip();
		if (!next.matches("\\d{" + Agents.PIN_LENGTH + "}") || Agents.isTrivial(next)) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "PIN_TOO_WEAK",
					"A PIN is 6 digits and not one digit repeated or a straight run", "newPin", null);
		}
		boolean changed = asPlatform(() -> {
			String hash = jdbc.sql("select pin_hash from agents where id = :id for update")
				.param("id", agent.agentId())
				.query(String.class)
				.single();
			if (!hasher.matches(currentPin == null ? "" : currentPin.strip(), hash)) {
				return false;
			}
			if (hasher.matches(next, hash)) {
				throw new ApiException(HttpStatus.BAD_REQUEST, "PIN_REUSED", "Choose a PIN different from the current one",
						"newPin", null);
			}
			jdbc.sql("update agents set pin_hash = :hash, must_change_pin = false, updated_at = :now where id = :id")
				.param("hash", hasher.hash(next))
				.param("now", ts(clock.instant()))
				.param("id", agent.agentId())
				.update();
			audit.record(new AuditLog.Actor(null, null), "AGENT_PIN_CHANGED", "AGENT", agent.agentId(),
					Scope.operator(device.operatorId()), Map.of("deviceId", device.deviceId().toString()));
			return true;
		});
		if (!changed) {
			throw new ApiException(HttpStatus.UNAUTHORIZED, "AGENT_SIGN_IN_FAILED", "The current PIN is not correct",
					"currentPin", null);
		}
	}

	/** Records the session's last use, at most once a minute (shown to the station as "last seen"). */
	void touch(String sessionToken) {
		Instant now = clock.instant();
		asPlatform(() -> jdbc.sql("""
				update agent_sessions set last_seen_at = :now
				where token_hash = :hash and revoked_at is null and last_seen_at < :stale
				""")
			.param("hash", SecureTokens.sha256Hex(sessionToken))
			.param("now", ts(now))
			.param("stale", ts(now.minus(Duration.ofMinutes(1))))
			.update());
	}

	void signOut(String sessionToken) {
		asPlatform(() -> jdbc.sql("""
				update agent_sessions set revoked_at = :now, revoke_reason = 'SIGNED_OUT'
				where token_hash = :hash and revoked_at is null
				""").param("hash", SecureTokens.sha256Hex(sessionToken)).param("now", ts(clock.instant())).update());
	}

	private <T> T asPlatform(Supplier<T> work) {
		return DbScope.runAs(new DbScope.Context(Scope.PLATFORM, null), () -> tx.execute((status) -> work.get()));
	}

}
