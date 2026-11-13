package com.zawed.api.identity;

import java.time.Clock;
import java.time.Duration;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.identity.IdentityRecords.UserView;
import com.zawed.api.platform.crypto.PasswordHasher;
import com.zawed.api.platform.crypto.SecureTokens;
import com.zawed.api.platform.ratelimit.RateLimiter;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;

/** What a signed-in user can do to their own account. */
@Service
class Account {

	static final int PASSWORD_CHANGE_ATTEMPTS = 10;

	private final IdentityStore store;
	private final PasswordHasher passwords;
	private final AuditLog audit;
	private final Clock clock;
	private final RateLimiter limiter;

	Account(IdentityStore store, PasswordHasher passwords, AuditLog audit, Clock clock, RateLimiter limiter) {
		this.store = store;
		this.passwords = passwords;
		this.audit = audit;
		this.clock = clock;
		this.limiter = limiter;
	}

	@Transactional(readOnly = true)
	UserView me(ZawedPrincipal principal) {
		return store.user(principal.userId()).orElseThrow(() -> ApiException.notFound("User"));
	}

	/**
	 * Changes the password and signs the user out everywhere else: whoever knew the old password
	 * must not keep a session.
	 */
	@Transactional
	UserView changePassword(ZawedPrincipal principal, String currentPassword, String newPassword) {
		// A stolen access token must not become a way to brute-force the current password.
		limiter.acquire("password-change:" + principal.userId(), PASSWORD_CHANGE_ATTEMPTS, Duration.ofMinutes(15));
		UserView user = me(principal);
		String hash = store.passwordHash(user.id()).orElseThrow(() -> ApiException.notFound("User"));
		if (!passwords.matches(String.valueOf(currentPassword), hash)) {
			throw new ApiException(HttpStatus.UNAUTHORIZED, "INVALID_CREDENTIALS", "The current password is not correct",
					"currentPassword", null);
		}
		if (SecureTokens.constantTimeEquals(currentPassword, newPassword)) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "PASSWORD_REUSED", "Choose a different password",
					"newPassword", null);
		}
		PasswordPolicy.check(newPassword, user.email());
		var now = clock.instant();
		store.setPassword(user.id(), passwords.hash(newPassword), false, user.id(), now);
		int revoked = store.revokeSessionsOf(user.id(), principal.sessionId(), "PASSWORD_CHANGED", now);
		audit.record(new AuditLog.Actor(user.id(), user.role()), "PASSWORD_CHANGED", "USER", user.id(), user.scope(),
				Map.of("otherSessionsEnded", revoked, "wasTemporary", user.mustChangePassword()));
		return me(principal);
	}

	@Transactional(readOnly = true)
	List<IdentityStore.SessionSummary> sessions(ZawedPrincipal principal) {
		return store.activeSessions(principal.userId(), clock.instant());
	}

	@Transactional
	void endSession(ZawedPrincipal principal, UUID sessionId) {
		if (!store.revokeOwnSession(principal.userId(), sessionId, clock.instant())) {
			throw ApiException.notFound("Session");
		}
		UserView user = me(principal);
		audit.record(new AuditLog.Actor(user.id(), user.role()), "SESSION_REVOKED", "USER", user.id(), user.scope(),
				Map.of("sessionId", sessionId.toString(), "reason", "SIGNED_OUT_ELSEWHERE"));
	}

}
