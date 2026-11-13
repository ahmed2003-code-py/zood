package com.zawed.api.identity;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;

import jakarta.servlet.http.HttpServletRequest;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Component;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.identity.IdentityRecords.Door;
import com.zawed.api.identity.IdentityRecords.SessionRow;
import com.zawed.api.identity.IdentityRecords.UserView;
import com.zawed.api.platform.crypto.SecureTokens;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.SessionVerifier;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.ClientInfo;

/**
 * Sessions and their refresh tokens (contract §3).
 *
 * <p>The refresh token is 32 random bytes, stored only as its SHA-256. Every refresh replaces it
 * (compare-and-swap, so two racing refreshes cannot both win). The token it replaced stays valid
 * for {@link #RACE_GRACE} so a request already in flight with it gets an access token instead of
 * a sign-out; presented any later, it is treated as stolen and the whole session is revoked.
 */
@Component
class Sessions implements SessionVerifier {

	static final Duration RACE_GRACE = Duration.ofSeconds(60);
	static final Duration PLATFORM_IDLE = Duration.ofHours(12);
	static final Duration SCOPED_IDLE = Duration.ofDays(7);
	static final Duration ABSOLUTE = Duration.ofDays(30);

	private static final Logger log = LoggerFactory.getLogger(Sessions.class);

	/** A new access token, and a new refresh token unless this was the loser of a refresh race. */
	record Issued(String accessToken, String refreshToken, Door door, UserView user) {
	}

	private final IdentityStore store;
	private final AccessTokens tokens;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;
	private final TransactionTemplate tx;

	Sessions(IdentityStore store, AccessTokens tokens, AuditLog audit, IdGenerator ids, Clock clock,
			TransactionTemplate tx) {
		this.store = store;
		this.tokens = tokens;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
		this.tx = tx;
	}

	/** Starts a session for a user whose identity was just proven. Runs in the caller's transaction. */
	Issued start(UUID userId, Role role, Scope scope, Door door, HttpServletRequest request) {
		Instant now = clock.instant();
		UUID sessionId = ids.next();
		String refreshToken = SecureTokens.random(32);
		store.insertSession(sessionId, userId, door, SecureTokens.sha256Hex(refreshToken), now,
				now.plus(idleFor(role)), now.plus(ABSOLUTE), ClientInfo.userAgent(request), ClientInfo.addressPrefix(request));
		audit.record(new AuditLog.Actor(userId, role), "LOGIN_SUCCEEDED", "USER", userId, scope,
				Map.of("door", door.name(), "sessionId", sessionId.toString()));
		UserView user = store.user(userId).orElseThrow(Sessions::expired);
		return new Issued(tokens.issue(userId, sessionId, role, scope), refreshToken, door, user);
	}

	/** Exchanges a refresh token for a new access token (and a new refresh token). */
	Issued refresh(String refreshToken, Door door) {
		if (refreshToken == null || refreshToken.isBlank()) {
			throw expired();
		}
		String presented = SecureTokens.sha256Hex(refreshToken);
		SessionRow session = store.sessionByRefreshHash(presented).orElseThrow(Sessions::expired);
		Instant now = clock.instant();
		if (session.door() != door || !session.usableAt(now)) {
			throw expired();
		}

		if (presented.equals(session.previousRefreshTokenHash())) {
			boolean racing = session.rotatedAt() != null && now.isBefore(session.rotatedAt().plus(RACE_GRACE));
			if (!racing) {
				revokeForReuse(session);
				throw new ApiException(HttpStatus.UNAUTHORIZED, "REFRESH_REUSE_DETECTED",
						"A refresh token was used twice; the session has been ended");
			}
			// Lost a race with a concurrent refresh: the winner's cookie is the live one.
			return new Issued(tokens.issue(session.userId(), session.sessionId(), session.role(), session.scope()), null,
					door, viewOf(session));
		}

		String next = SecureTokens.random(32);
		Instant idleEnd = min(now.plus(idleFor(session.role())), session.absoluteExpiresAt());
		if (!store.rotate(session.sessionId(), presented, SecureTokens.sha256Hex(next), now, idleEnd)) {
			// Someone rotated between our read and our write: same as losing the race.
			return new Issued(tokens.issue(session.userId(), session.sessionId(), session.role(), session.scope()), null,
					door, viewOf(session));
		}
		return new Issued(tokens.issue(session.userId(), session.sessionId(), session.role(), session.scope()), next,
				door, viewOf(session));
	}

	/** Ends the session a refresh token belongs to. Unknown tokens are ignored: logout always succeeds. */
	void end(String refreshToken) {
		if (refreshToken == null || refreshToken.isBlank()) {
			return;
		}
		store.sessionByRefreshHash(SecureTokens.sha256Hex(refreshToken)).ifPresent((session) -> asUser(session,
				() -> {
					if (store.revokeSession(session.sessionId(), "LOGOUT", clock.instant())) {
						audit.record(new AuditLog.Actor(session.userId(), session.role()), "LOGOUT", "USER",
								session.userId(), session.scope(), Map.of("sessionId", session.sessionId().toString()));
					}
				}));
	}

	@Override
	public Optional<ZawedPrincipal> verify(UUID sessionId, UUID userId) {
		Instant now = clock.instant();
		return store.sessionById(sessionId)
			.filter((session) -> session.userId().equals(userId) && session.usableAt(now))
			.map((session) -> new ZawedPrincipal(session.userId(), session.sessionId(), session.role(), session.scope(),
					session.mustChangePassword()));
	}

	private void revokeForReuse(SessionRow session) {
		log.warn("Refresh token reuse detected; revoking session {} of user {}", session.sessionId(), session.userId());
		asUser(session, () -> {
			store.revokeSession(session.sessionId(), "REFRESH_REUSE_DETECTED", clock.instant());
			audit.record(new AuditLog.Actor(null, null), "SESSION_REVOKED", "USER", session.userId(), session.scope(),
					Map.of("reason", "REFRESH_REUSE_DETECTED", "sessionId", session.sessionId().toString()));
		});
	}

	private void asUser(SessionRow session, Runnable work) {
		DbScope.runAs(new DbScope.Context(session.scope(), session.userId()), () -> {
			tx.executeWithoutResult((status) -> work.run());
			return null;
		});
	}

	private static UserView viewOf(SessionRow session) {
		return new UserView(session.userId(), session.email(), session.fullName(), session.role(), session.scope(),
				session.status(), session.mustChangePassword(), session.mfaEnabled(), null, session.createdAt());
	}

	static Duration idleFor(Role role) {
		return role.isPlatform() ? PLATFORM_IDLE : SCOPED_IDLE;
	}

	private static Instant min(Instant a, Instant b) {
		return a.isBefore(b) ? a : b;
	}

	static ApiException expired() {
		return new ApiException(HttpStatus.UNAUTHORIZED, "SESSION_EXPIRED", "Sign in again");
	}

}
