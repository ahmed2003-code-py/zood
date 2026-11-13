package com.zawed.api.identity;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Instant;
import java.util.UUID;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;

/** Rows of the identity tables, as the module reads them. */
final class IdentityRecords {

	private IdentityRecords() {
	}

	enum UserStatus {

		ACTIVE, SUSPENDED, DEACTIVATED

	}

	/** Which sign-in door, and so which cookie, a session belongs to. */
	enum Door {

		USER, ADMIN

	}

	/** What the sign-in path needs (auth_login_lookup). */
	record LoginCandidate(UUID id, String email, String fullName, String passwordHash, Role role, Scope scope,
			UserStatus status, boolean mustChangePassword, Instant lockedUntil, boolean mfaEnabled) {

		static LoginCandidate map(ResultSet rs, int row) throws SQLException {
			return new LoginCandidate(rs.getObject("id", UUID.class), rs.getString("email"), rs.getString("full_name"),
					rs.getString("password_hash"), Role.valueOf(rs.getString("role")), scopeOf(rs),
					UserStatus.valueOf(rs.getString("status")), rs.getBoolean("must_change_password"),
					instant(rs, "locked_until"), rs.getBoolean("mfa_enabled"));
		}

	}

	/** A user as the admin screens and the profile see it. Never carries secrets. */
	record UserView(UUID id, String email, String fullName, Role role, Scope scope, UserStatus status,
			boolean mustChangePassword, boolean mfaEnabled, Instant lockedUntil, Instant createdAt) {

		static final String COLUMNS = "id, email, full_name, role, scope_type, scope_id, status, must_change_password, "
				+ "mfa_enabled, locked_until, created_at";

		static UserView map(ResultSet rs, int row) throws SQLException {
			return new UserView(rs.getObject("id", UUID.class), rs.getString("email"), rs.getString("full_name"),
					Role.valueOf(rs.getString("role")), scopeOf(rs), UserStatus.valueOf(rs.getString("status")),
					rs.getBoolean("must_change_password"), rs.getBoolean("mfa_enabled"), instant(rs, "locked_until"),
					instant(rs, "created_at"));
		}

	}

	/** A session joined with its user, as the auth_session_* functions return it. */
	record SessionRow(UUID sessionId, UUID userId, Door door, String refreshTokenHash, String previousRefreshTokenHash,
			Instant rotatedAt, Instant createdAt, Instant expiresAt, Instant absoluteExpiresAt, Instant revokedAt,
			String email, String fullName, Role role, Scope scope, UserStatus status, boolean mustChangePassword,
			boolean mfaEnabled) {

		static SessionRow map(ResultSet rs, int row) throws SQLException {
			return new SessionRow(rs.getObject("session_id", UUID.class), rs.getObject("user_id", UUID.class),
					Door.valueOf(rs.getString("door")), rs.getString("refresh_token_hash"),
					rs.getString("previous_refresh_token_hash"), instant(rs, "rotated_at"), instant(rs, "created_at"),
					instant(rs, "expires_at"), instant(rs, "absolute_expires_at"), instant(rs, "revoked_at"),
					rs.getString("email"), rs.getString("full_name"), Role.valueOf(rs.getString("role")), scopeOf(rs),
					UserStatus.valueOf(rs.getString("status")), rs.getBoolean("must_change_password"),
					rs.getBoolean("mfa_enabled"));
		}

		boolean usableAt(Instant now) {
			return revokedAt == null && status == UserStatus.ACTIVE && now.isBefore(expiresAt)
					&& now.isBefore(absoluteExpiresAt);
		}

	}

	/** The second sign-in step. */
	record Challenge(UUID id, UUID userId, String purpose, Door door, Scope scope, byte[] pendingSecretEncrypted,
			int attempts, Instant expiresAt, Instant usedAt) {

		static Challenge map(ResultSet rs, int row) throws SQLException {
			return new Challenge(rs.getObject("id", UUID.class), rs.getObject("user_id", UUID.class),
					rs.getString("purpose"), Door.valueOf(rs.getString("door")), scopeOf(rs),
					rs.getBytes("pending_secret_encrypted"), rs.getInt("attempts"), instant(rs, "expires_at"),
					instant(rs, "used_at"));
		}

	}

	static Scope scopeOf(ResultSet rs) throws SQLException {
		ScopeType type = ScopeType.valueOf(rs.getString("scope_type"));
		return new Scope(type, rs.getObject("scope_id", UUID.class));
	}

	static Instant instant(ResultSet rs, String column) throws SQLException {
		var timestamp = rs.getTimestamp(column);
		return timestamp == null ? null : timestamp.toInstant();
	}

	static java.sql.Timestamp ts(Instant instant) {
		return instant == null ? null : java.sql.Timestamp.from(instant);
	}

}
