package com.zawed.api.identity;

import java.nio.ByteBuffer;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.Locale;
import java.util.Map;
import java.util.OptionalLong;
import java.util.UUID;
import java.util.function.Supplier;

import jakarta.servlet.http.HttpServletRequest;

import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.identity.IdentityRecords.Challenge;
import com.zawed.api.identity.IdentityRecords.Door;
import com.zawed.api.identity.IdentityRecords.LoginCandidate;
import com.zawed.api.identity.IdentityRecords.UserStatus;
import com.zawed.api.identity.IdentityRecords.UserView;
import com.zawed.api.platform.config.ZawedProperties;
import com.zawed.api.platform.crypto.DataEncryptor;
import com.zawed.api.platform.crypto.SecureTokens;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.ratelimit.RateLimiter;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.ClientInfo;
import com.zawed.api.platform.web.RequestIdFilter;

/**
 * The sign-in path: password, then (for Ops and Super Admin) a second factor, then a session.
 *
 * <ul>
 * <li>Every refusal before the password is proven looks the same and takes the same time: an
 * unknown email, a wrong password and the wrong door all answer {@code INVALID_CREDENTIALS} after a
 * full Argon2 check. Only after a correct password does the caller learn that an account is
 * disabled.
 * <li>Five wrong passwords lock the account for 15 minutes; Ops can unlock it.
 * <li>A second-factor code is accepted once: its time step must be later than any accepted before.
 * </ul>
 */
@Service
class SignIn {

	static final int MAX_FAILED_LOGINS = 5;
	static final Duration LOCK = Duration.ofMinutes(15);
	static final Duration VERIFY_CHALLENGE_LIFETIME = Duration.ofMinutes(5);
	static final Duration ENROLL_CHALLENGE_LIFETIME = Duration.ofMinutes(10);
	static final int MAX_CHALLENGE_ATTEMPTS = 5;
	static final int EMAIL_ATTEMPTS = 10;
	static final Duration EMAIL_WINDOW = Duration.ofMinutes(15);
	static final String TOTP_ISSUER = "ZAWED";

	/** What sign-in returns: a session, or a challenge for the second step. */
	sealed interface Outcome {

		record Authenticated(Sessions.Issued session) implements Outcome {
		}

		record MfaRequired(String challengeToken, long expiresInSeconds) implements Outcome {
		}

		record EnrollmentRequired(String challengeToken, long expiresInSeconds) implements Outcome {
		}

	}

	record Enrollment(String secret, String otpauthUri) {
	}

	private final IdentityStore store;
	private final PasswordHasher passwords;
	private final Sessions sessions;
	private final DataEncryptor encryptor;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;
	private final TransactionTemplate tx;
	private final RateLimiter limiter;
	private final ZawedProperties properties;

	SignIn(IdentityStore store, PasswordHasher passwords, Sessions sessions, DataEncryptor encryptor, AuditLog audit,
			IdGenerator ids, Clock clock, TransactionTemplate tx, RateLimiter limiter, ZawedProperties properties) {
		this.store = store;
		this.passwords = passwords;
		this.sessions = sessions;
		this.encryptor = encryptor;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
		this.tx = tx;
		this.limiter = limiter;
		this.properties = properties;
	}

	Outcome login(Door door, String rawEmail, String password, HttpServletRequest request) {
		String email = normalizeEmail(rawEmail);
		limiter.acquire("signin-email:" + SecureTokens.sha256Hex(email), EMAIL_ATTEMPTS, EMAIL_WINDOW);

		LoginCandidate user = store.loginLookup(email).orElse(null);
		Instant now = clock.instant();
		if (user == null) {
			passwords.burn(password);
			throw invalidCredentials();
		}
		if (user.lockedUntil() != null && user.lockedUntil().isAfter(now)) {
			passwords.burn(password);
			throw locked(user.lockedUntil(), now);
		}
		if (!passwords.matches(password, user.passwordHash())) {
			Instant lockedUntil = recordFailure(user, true, "WRONG_PASSWORD", request);
			if (lockedUntil != null && lockedUntil.isAfter(now)) {
				throw locked(lockedUntil, now);
			}
			throw invalidCredentials();
		}
		// The password is right. From here on the caller may learn facts about the account.
		boolean adminDoor = door == Door.ADMIN;
		if (adminDoor != (user.role() == Role.SUPER_ADMIN)) {
			recordFailure(user, false, "WRONG_DOOR", request);
			throw invalidCredentials();
		}
		if (user.status() != UserStatus.ACTIVE) {
			recordFailure(user, false, "ACCOUNT_" + user.status().name(), request);
			throw new ApiException(HttpStatus.FORBIDDEN, "ACCOUNT_DISABLED", "This account is not active");
		}

		return asUser(user.scope(), user.id(), () -> {
			store.clearLoginFailures(user.id(), now);
			if (user.mfaEnabled() || user.role().requiresMfa()) {
				return challenge(user.id(), user.scope(), door, user.mfaEnabled() ? "VERIFY" : "ENROLL", request);
			}
			return new Outcome.Authenticated(sessions.start(user.id(), user.role(), user.scope(), door, request));
		});
	}

	Outcome verifyMfa(String challengeToken, String code, HttpServletRequest request) {
		Challenge challenge = liveChallenge(challengeToken, "VERIFY");
		byte[] secret = asUser(challenge.scope(), challenge.userId(), () -> store.mfaState(challenge.userId())
			.map((state) -> encryptor.decrypt(state.secretEncrypted(), userBinding(challenge.userId())))
			.orElseThrow(SignIn::challengeExpired));
		OptionalLong step = Totp.matchingStep(secret, code, clock.instant().getEpochSecond());
		if (step.isEmpty()) {
			throw wrongCode(challenge);
		}
		return asUser(challenge.scope(), challenge.userId(), () -> {
			Instant now = clock.instant();
			if (!store.acceptStep(challenge.userId(), step.getAsLong(), now)) {
				// The code was right but already used: a replay, or a second tab racing the first.
				throw new ApiException(HttpStatus.UNAUTHORIZED, "MFA_INVALID_CODE", "This code was already used");
			}
			return complete(challenge, now, request);
		});
	}

	Enrollment startEnrollment(String challengeToken) {
		Challenge challenge = liveChallenge(challengeToken, "ENROLL");
		return asUser(challenge.scope(), challenge.userId(), () -> {
			UserView user = store.user(challenge.userId()).orElseThrow(SignIn::challengeExpired);
			byte[] secret;
			if (challenge.pendingSecretEncrypted() != null) {
				// Asked again (a reload): show the same key, so a phone that already scanned it keeps working.
				secret = encryptor.decrypt(challenge.pendingSecretEncrypted(), challengeBinding(challenge.id()));
			}
			else {
				secret = Totp.newSecret();
				store.setPendingSecret(challenge.id(), encryptor.encrypt(secret, challengeBinding(challenge.id())));
			}
			return new Enrollment(Totp.base32(secret), Totp.otpauthUri(TOTP_ISSUER, user.email(), secret));
		});
	}

	Outcome confirmEnrollment(String challengeToken, String code, HttpServletRequest request) {
		Challenge challenge = liveChallenge(challengeToken, "ENROLL");
		if (challenge.pendingSecretEncrypted() == null) {
			throw challengeExpired();
		}
		byte[] secret = encryptor.decrypt(challenge.pendingSecretEncrypted(), challengeBinding(challenge.id()));
		OptionalLong step = Totp.matchingStep(secret, code, clock.instant().getEpochSecond());
		if (step.isEmpty()) {
			throw wrongCode(challenge);
		}
		return asUser(challenge.scope(), challenge.userId(), () -> {
			Instant now = clock.instant();
			store.enableMfa(challenge.userId(), encryptor.encrypt(secret, userBinding(challenge.userId())),
					step.getAsLong(), now);
			UserView user = store.user(challenge.userId()).orElseThrow(SignIn::challengeExpired);
			audit.record(new AuditLog.Actor(user.id(), user.role()), "MFA_ENROLLED", "USER", user.id(), user.scope(),
					Map.of());
			return complete(challenge, now, request);
		});
	}

	/** One-time creation of the first Super Admin (contract §4). */
	UserView bootstrap(String setupToken, String rawEmail, String fullName, String password,
			HttpServletRequest request) {
		ZawedProperties.Security security = properties.security();
		// Hashing both sides first makes the comparison constant-time whatever the lengths.
		boolean tokenMatches = security.bootstrapEnabled() && SecureTokens.constantTimeEquals(
				SecureTokens.sha256Hex(security.bootstrapToken()), SecureTokens.sha256Hex(String.valueOf(setupToken)));
		if (!tokenMatches || store.superAdminExists()) {
			throw new ApiException(HttpStatus.NOT_FOUND, "SETUP_NOT_AVAILABLE", "Setup is not available");
		}
		String email = normalizeEmail(rawEmail);
		PasswordPolicy.check(password, email);
		UUID id = ids.next();
		String hash = passwords.hash(password);
		boolean created = Boolean.TRUE.equals(tx.execute((status) -> store.bootstrapSuperAdmin(id, email, fullName.trim(),
				hash, clock.instant(), ids.next(), RequestIdFilter.currentRequestId(request),
				ClientInfo.addressPrefix(request))));
		if (!created) {
			throw new ApiException(HttpStatus.NOT_FOUND, "SETUP_NOT_AVAILABLE", "Setup is not available");
		}
		return asUser(Scope.PLATFORM, id, () -> store.user(id).orElseThrow());
	}

	private Outcome complete(Challenge challenge, Instant now, HttpServletRequest request) {
		if (!store.useChallenge(challenge.id(), now)) {
			throw challengeExpired();
		}
		UserView user = store.user(challenge.userId()).orElseThrow(SignIn::challengeExpired);
		if (user.status() != UserStatus.ACTIVE) {
			throw new ApiException(HttpStatus.FORBIDDEN, "ACCOUNT_DISABLED", "This account is not active");
		}
		return new Outcome.Authenticated(sessions.start(user.id(), user.role(), user.scope(), challenge.door(), request));
	}

	private Outcome challenge(UUID userId, Scope scope, Door door, String purpose, HttpServletRequest request) {
		Instant now = clock.instant();
		Duration lifetime = "VERIFY".equals(purpose) ? VERIFY_CHALLENGE_LIFETIME : ENROLL_CHALLENGE_LIFETIME;
		String token = SecureTokens.random(32);
		store.insertChallenge(ids.next(), userId, purpose, door, scope, SecureTokens.sha256Hex(token), now,
				now.plus(lifetime), ClientInfo.userAgent(request), ClientInfo.addressPrefix(request));
		return "VERIFY".equals(purpose) ? new Outcome.MfaRequired(token, lifetime.toSeconds())
				: new Outcome.EnrollmentRequired(token, lifetime.toSeconds());
	}

	private Challenge liveChallenge(String token, String purpose) {
		if (token == null || token.isBlank()) {
			throw challengeExpired();
		}
		Challenge challenge = store.challengeByTokenHash(SecureTokens.sha256Hex(token)).orElseThrow(SignIn::challengeExpired);
		boolean live = purpose.equals(challenge.purpose()) && challenge.usedAt() == null
				&& challenge.expiresAt().isAfter(clock.instant()) && challenge.attempts() < MAX_CHALLENGE_ATTEMPTS;
		if (!live) {
			throw challengeExpired();
		}
		return challenge;
	}

	/** Counts a wrong code in its own transaction, so the count survives the refusal. */
	private ApiException wrongCode(Challenge challenge) {
		asUser(challenge.scope(), challenge.userId(), () -> {
			int attempts = store.countChallengeAttempt(challenge.id());
			audit.record(new AuditLog.Actor(null, null), "MFA_FAILED", "USER", challenge.userId(), challenge.scope(),
					Map.of("attempt", attempts, "purpose", challenge.purpose()));
			return null;
		});
		return new ApiException(HttpStatus.UNAUTHORIZED, "MFA_INVALID_CODE", "The code is not correct");
	}

	private Instant recordFailure(LoginCandidate user, boolean count, String reason, HttpServletRequest request) {
		return tx.execute((status) -> store.recordLoginFailure(user.id(), count, reason, MAX_FAILED_LOGINS,
				(int) LOCK.toSeconds(), clock.instant(), ids.next(), ids.next(), RequestIdFilter.currentRequestId(request),
				ClientInfo.addressPrefix(request)));
	}

	/** Runs {@code work} in a transaction scoped to the user just identified (row-level security). */
	private <T> T asUser(Scope scope, UUID userId, Supplier<T> work) {
		return DbScope.runAs(new DbScope.Context(scope, userId), () -> tx.execute((status) -> work.get()));
	}

	/** Binds an encrypted TOTP secret to its user: copied into another row, it does not decrypt. */
	static byte[] userBinding(UUID userId) {
		return ByteBuffer.allocate(20).put("user".getBytes()).putLong(userId.getMostSignificantBits())
			.putLong(userId.getLeastSignificantBits()).array();
	}

	private static byte[] challengeBinding(UUID challengeId) {
		return ByteBuffer.allocate(20).put("chal".getBytes()).putLong(challengeId.getMostSignificantBits())
			.putLong(challengeId.getLeastSignificantBits()).array();
	}

	static String normalizeEmail(String email) {
		String normalized = email == null ? "" : email.trim().toLowerCase(Locale.ROOT);
		if (normalized.isEmpty() || normalized.length() > 254 || !normalized.contains("@")) {
			// Shaped like a wrong password, so a malformed email reveals nothing either.
			throw invalidCredentials();
		}
		return normalized;
	}

	private static ApiException invalidCredentials() {
		return new ApiException(HttpStatus.UNAUTHORIZED, "INVALID_CREDENTIALS", "The email or password is not correct");
	}

	private static ApiException locked(Instant until, Instant now) {
		long seconds = Math.max(1, Duration.between(now, until).toSeconds());
		return new ApiException(HttpStatus.LOCKED, "ACCOUNT_LOCKED", "Too many failed attempts", null,
				Map.of("retryAfterSeconds", seconds));
	}

	private static ApiException challengeExpired() {
		return new ApiException(HttpStatus.UNAUTHORIZED, "MFA_CHALLENGE_EXPIRED", "Sign in again");
	}

}
