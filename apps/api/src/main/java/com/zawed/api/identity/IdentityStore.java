package com.zawed.api.identity;

import static com.zawed.api.identity.IdentityRecords.ts;

import java.time.Instant;
import java.util.List;
import java.util.Optional;
import java.util.UUID;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Repository;

import com.zawed.api.identity.IdentityRecords.Challenge;
import com.zawed.api.identity.IdentityRecords.Door;
import com.zawed.api.identity.IdentityRecords.LoginCandidate;
import com.zawed.api.identity.IdentityRecords.SessionRow;
import com.zawed.api.identity.IdentityRecords.UserStatus;
import com.zawed.api.identity.IdentityRecords.UserView;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;

/**
 * Every query of the identity module. Reads and writes of {@code users} run inside a transaction
 * whose scope row-level security checks; the sign-in path, which has no scope yet, goes through the
 * narrow {@code auth_*} SECURITY DEFINER functions from V3 instead.
 */
@Repository
class IdentityStore {

	private final JdbcClient jdbc;

	IdentityStore(JdbcClient jdbc) {
		this.jdbc = jdbc;
	}

	// ---------------------------------------------------------------------------------- sign-in path

	Optional<LoginCandidate> loginLookup(String email) {
		return jdbc.sql("select * from auth_login_lookup(:email)").param("email", email).query(LoginCandidate::map).optional();
	}

	/** Counts a failure (and maybe locks), audited by the function itself. Returns the lock end, if locked. */
	Instant recordLoginFailure(UUID userId, boolean count, String reason, int maxAttempts, int lockSeconds, Instant now,
			UUID auditId, UUID lockAuditId, String requestId, String ipPrefix) {
		return jdbc.sql("""
				select auth_record_login_failure(:user, :count, :reason, :max, :lock, :now, :audit, :lockAudit,
				                                 :requestId, :ip) as locked_until
				""")
			.param("user", userId)
			.param("count", count)
			.param("reason", reason)
			.param("max", maxAttempts)
			.param("lock", lockSeconds)
			.param("now", ts(now))
			.param("audit", auditId)
			.param("lockAudit", lockAuditId)
			.param("requestId", requestId)
			.param("ip", ipPrefix)
			.query((rs, n) -> IdentityRecords.instant(rs, "locked_until"))
			.optional()
			.orElse(null);
	}

	boolean superAdminExists() {
		return Boolean.TRUE.equals(jdbc.sql("select auth_super_admin_exists()").query(Boolean.class).single());
	}

	boolean bootstrapSuperAdmin(UUID id, String email, String fullName, String passwordHash, Instant now, UUID auditId,
			String requestId, String ipPrefix) {
		return Boolean.TRUE.equals(jdbc.sql("""
				select auth_bootstrap_super_admin(:id, :email, :name, :hash, :now, :audit, :requestId, :ip)
				""")
			.param("id", id)
			.param("email", email)
			.param("name", fullName)
			.param("hash", passwordHash)
			.param("now", ts(now))
			.param("audit", auditId)
			.param("requestId", requestId)
			.param("ip", ipPrefix)
			.query(Boolean.class)
			.single());
	}

	// -------------------------------------------------------------------------------------- sessions

	Optional<SessionRow> sessionById(UUID sessionId) {
		return jdbc.sql("select * from auth_session_by_id(:id)").param("id", sessionId).query(SessionRow::map).optional();
	}

	Optional<SessionRow> sessionByRefreshHash(String hash) {
		return jdbc.sql("select * from auth_session_by_refresh_hash(:hash)")
			.param("hash", hash)
			.query(SessionRow::map)
			.optional();
	}

	void insertSession(UUID id, UUID userId, Door door, String refreshHash, Instant now, Instant expiresAt,
			Instant absoluteExpiresAt, String userAgent, String ipPrefix) {
		jdbc.sql("""
				insert into sessions (id, user_id, door, refresh_token_hash, created_at, last_seen_at, expires_at,
				                      absolute_expires_at, user_agent, ip_prefix)
				values (:id, :user, :door, :hash, :now, :now, :expires, :absolute, :agent, :ip)
				""")
			.param("id", id)
			.param("user", userId)
			.param("door", door.name())
			.param("hash", refreshHash)
			.param("now", ts(now))
			.param("expires", ts(expiresAt))
			.param("absolute", ts(absoluteExpiresAt))
			.param("agent", userAgent)
			.param("ip", ipPrefix)
			.update();
	}

	/**
	 * Compare-and-swap rotation: succeeds only if nobody rotated this session since {@code currentHash}
	 * was read. Two refreshes racing with the same token cannot both win.
	 */
	boolean rotate(UUID sessionId, String currentHash, String newHash, Instant now, Instant expiresAt) {
		return jdbc.sql("""
				update sessions
				set previous_refresh_token_hash = refresh_token_hash, refresh_token_hash = :new, rotated_at = :now,
				    last_seen_at = :now, expires_at = :expires
				where id = :id and refresh_token_hash = :current and revoked_at is null
				""")
			.param("id", sessionId)
			.param("current", currentHash)
			.param("new", newHash)
			.param("now", ts(now))
			.param("expires", ts(expiresAt))
			.update() == 1;
	}

	boolean revokeSession(UUID sessionId, String reason, Instant now) {
		return jdbc.sql("update sessions set revoked_at = :now, revoked_reason = :reason where id = :id and revoked_at is null")
			.param("id", sessionId)
			.param("reason", reason)
			.param("now", ts(now))
			.update() == 1;
	}

	int revokeSessionsOf(UUID userId, UUID except, String reason, Instant now) {
		return jdbc.sql("""
				update sessions set revoked_at = :now, revoked_reason = :reason
				where user_id = :user and revoked_at is null and (cast(:except as uuid) is null or id <> :except)
				""")
			.param("user", userId)
			.param("except", except)
			.param("reason", reason)
			.param("now", ts(now))
			.update();
	}

	record SessionSummary(UUID id, Instant createdAt, Instant lastSeenAt, String userAgent, String ipPrefix) {
	}

	List<SessionSummary> activeSessions(UUID userId, Instant now) {
		return jdbc.sql("""
				select id, created_at, last_seen_at, user_agent, ip_prefix from sessions
				where user_id = :user and revoked_at is null and expires_at > :now and absolute_expires_at > :now
				order by id desc limit 50
				""")
			.param("user", userId)
			.param("now", ts(now))
			.query((rs, n) -> new SessionSummary(rs.getObject("id", UUID.class), IdentityRecords.instant(rs, "created_at"),
					IdentityRecords.instant(rs, "last_seen_at"), rs.getString("user_agent"), rs.getString("ip_prefix")))
			.list();
	}

	boolean revokeOwnSession(UUID userId, UUID sessionId, Instant now) {
		return jdbc.sql("""
				update sessions set revoked_at = :now, revoked_reason = 'SIGNED_OUT_ELSEWHERE'
				where id = :id and user_id = :user and revoked_at is null
				""")
			.param("id", sessionId)
			.param("user", userId)
			.param("now", ts(now))
			.update() == 1;
	}

	// ------------------------------------------------------------------------------------ challenges

	void insertChallenge(UUID id, UUID userId, String purpose, Door door, Scope scope, String tokenHash, Instant now,
			Instant expiresAt, String userAgent, String ipPrefix) {
		jdbc.sql("""
				insert into mfa_challenges (id, user_id, purpose, door, token_hash, scope_type, scope_id, created_at,
				                            expires_at, user_agent, ip_prefix)
				values (:id, :user, :purpose, :door, :hash, :scopeType, :scopeId, :now, :expires, :agent, :ip)
				""")
			.param("id", id)
			.param("user", userId)
			.param("purpose", purpose)
			.param("door", door.name())
			.param("hash", tokenHash)
			.param("scopeType", scope.type().name())
			.param("scopeId", scope.id())
			.param("now", ts(now))
			.param("expires", ts(expiresAt))
			.param("agent", userAgent)
			.param("ip", ipPrefix)
			.update();
	}

	Optional<Challenge> challengeByTokenHash(String hash) {
		return jdbc.sql("select * from mfa_challenges where token_hash = :hash")
			.param("hash", hash)
			.query(Challenge::map)
			.optional();
	}

	/** Counts a wrong code; returns the new count. */
	int countChallengeAttempt(UUID id) {
		return jdbc.sql("update mfa_challenges set attempts = attempts + 1 where id = :id returning attempts")
			.param("id", id)
			.query(Integer.class)
			.single();
	}

	/** Single use: only the first caller marks it used. */
	boolean useChallenge(UUID id, Instant now) {
		return jdbc.sql("update mfa_challenges set used_at = :now where id = :id and used_at is null")
			.param("id", id)
			.param("now", ts(now))
			.update() == 1;
	}

	void setPendingSecret(UUID id, byte[] encrypted) {
		jdbc.sql("update mfa_challenges set pending_secret_encrypted = :secret where id = :id")
			.param("id", id)
			.param("secret", encrypted)
			.update();
	}

	// ----------------------------------------------------------------------------------------- users

	Optional<UserView> user(UUID id) {
		return jdbc.sql("select " + UserView.COLUMNS + " from users where id = :id")
			.param("id", id)
			.query(UserView::map)
			.optional();
	}

	Optional<String> passwordHash(UUID id) {
		return jdbc.sql("select password_hash from users where id = :id").param("id", id).query(String.class).optional();
	}

	record MfaState(byte[] secretEncrypted, Long lastUsedStep) {
	}

	Optional<MfaState> mfaState(UUID id) {
		return jdbc.sql("select mfa_secret_encrypted, mfa_last_used_step from users where id = :id and mfa_enabled")
			.param("id", id)
			.query((rs, n) -> new MfaState(rs.getBytes(1), rs.getObject(2, Long.class)))
			.optional();
	}

	/** Accepts a TOTP step only if it is later than every step accepted before (no replay). */
	boolean acceptStep(UUID userId, long step, Instant now) {
		return jdbc.sql("""
				update users set mfa_last_used_step = :step, updated_at = :now
				where id = :id and (mfa_last_used_step is null or mfa_last_used_step < :step)
				""")
			.param("id", userId)
			.param("step", step)
			.param("now", ts(now))
			.update() == 1;
	}

	void enableMfa(UUID userId, byte[] secretEncrypted, long step, Instant now) {
		jdbc.sql("""
				update users set mfa_secret_encrypted = :secret, mfa_enabled = true, mfa_last_used_step = :step,
				                 updated_at = :now, updated_by = :id
				where id = :id
				""")
			.param("id", userId)
			.param("secret", secretEncrypted)
			.param("step", step)
			.param("now", ts(now))
			.update();
	}

	void clearLoginFailures(UUID userId, Instant now) {
		jdbc.sql("""
				update users set failed_login_count = 0, locked_until = null, updated_at = :now
				where id = :id and (failed_login_count <> 0 or locked_until is not null)
				""")
			.param("id", userId)
			.param("now", ts(now))
			.update();
	}

	void setPassword(UUID userId, String hash, boolean mustChange, UUID actor, Instant now) {
		jdbc.sql("""
				update users set password_hash = :hash, must_change_password = :mustChange, password_changed_at = :now,
				                 failed_login_count = 0, locked_until = null, updated_at = :now, updated_by = :actor
				where id = :id
				""")
			.param("id", userId)
			.param("hash", hash)
			.param("mustChange", mustChange)
			.param("actor", actor)
			.param("now", ts(now))
			.update();
	}

	void insertUser(UUID id, String email, String fullName, String passwordHash, Role role, Scope scope, UUID actor,
			Instant now) {
		jdbc.sql("""
				insert into users (id, email, full_name, password_hash, role, scope_type, scope_id, status,
				                   must_change_password, password_changed_at, created_at, created_by, updated_at, updated_by)
				values (:id, :email, :name, :hash, :role, :scopeType, :scopeId, 'ACTIVE', true, :now, :now, :actor, :now,
				        :actor)
				""")
			.param("id", id)
			.param("email", email)
			.param("name", fullName)
			.param("hash", passwordHash)
			.param("role", role.name())
			.param("scopeType", scope.type().name())
			.param("scopeId", scope.id())
			.param("actor", actor)
			.param("now", ts(now))
			.update();
	}

	boolean emailTaken(String email) {
		return Boolean.TRUE.equals(
				jdbc.sql("select exists (select 1 from auth_login_lookup(:email))").param("email", email).query(Boolean.class).single());
	}

	void setStatus(UUID userId, UserStatus status, UUID actor, Instant now) {
		jdbc.sql("update users set status = :status, updated_at = :now, updated_by = :actor where id = :id")
			.param("id", userId)
			.param("status", status.name())
			.param("actor", actor)
			.param("now", ts(now))
			.update();
	}

	void unlock(UUID userId, UUID actor, Instant now) {
		jdbc.sql("""
				update users set failed_login_count = 0, locked_until = null, updated_at = :now, updated_by = :actor
				where id = :id
				""")
			.param("id", userId)
			.param("actor", actor)
			.param("now", ts(now))
			.update();
	}

	void resetMfa(UUID userId, UUID actor, Instant now) {
		jdbc.sql("""
				update users set mfa_enabled = false, mfa_secret_encrypted = null, mfa_last_used_step = null,
				                 updated_at = :now, updated_by = :actor
				where id = :id
				""")
			.param("id", userId)
			.param("actor", actor)
			.param("now", ts(now))
			.update();
	}

	/** Locks the active Super Admin rows so two demotions cannot both pass the "last one" check. */
	int lockActiveSuperAdmins() {
		return jdbc.sql("select id from users where role = 'SUPER_ADMIN' and status = 'ACTIVE' for update")
			.query(UUID.class)
			.list()
			.size();
	}

	List<UserView> listUsers(List<Role> roles, UserStatus status, String query, UUID cursor, int limit) {
		return jdbc.sql("select " + UserView.COLUMNS + """
				 from users
				where role in (:roles)
				  and (cast(:status as text) is null or status = :status)
				  and (cast(:q as text) is null or lower(email) like :q or lower(full_name) like :q)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc
				limit :limit
				""")
			.param("roles", roles.stream().map(Role::name).toList())
			.param("status", status == null ? null : status.name())
			.param("q", query == null || query.isBlank() ? null : "%" + escapeLike(query.trim().toLowerCase()) + "%")
			.param("cursor", cursor)
			.param("limit", limit)
			.query(UserView::map)
			.list();
	}

	private static String escapeLike(String value) {
		return value.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_");
	}

}
