package com.zawed.api.identity;

import java.time.Instant;
import java.util.UUID;

import com.fasterxml.jackson.annotation.JsonInclude;

import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;

import jakarta.validation.Valid;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Size;

import com.zawed.api.identity.IdentityRecords.UserStatus;
import com.zawed.api.identity.IdentityRecords.UserView;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;

/**
 * Request and response bodies of the identity endpoints (docs/architecture/b1-identity-contract.md).
 * Sizes are bounded so no field can carry an unbounded payload into Argon2 or the database.
 */
final class IdentityApi {

	private IdentityApi() {
	}

	// ----------------------------------------------------------------------------------- requests

	record LoginRequest(@NotBlank @Size(max = 254) String email, @NotBlank @Size(max = 256) String password) {
	}

	record ChallengeRequest(@NotBlank @Size(max = 128) String challengeToken) {
	}

	record ChallengeCodeRequest(@NotBlank @Size(max = 128) String challengeToken,
			@NotBlank @Size(min = 6, max = 6) String code) {
	}

	record ChangePasswordRequest(@NotBlank @Size(max = 256) String currentPassword,
			@NotBlank @Size(max = 256) String newPassword) {
	}

	record SetupRequest(@NotBlank @Size(max = 256) String setupToken, @NotBlank @Size(max = 254) String email,
			@NotBlank @Size(max = 200) String fullName, @NotBlank @Size(max = 256) String password) {
	}

	record ScopeBody(@NotNull ScopeType type, UUID id) {

		Scope toScope() {
			try {
				return new Scope(type, id);
			}
			catch (IllegalArgumentException ex) {
				throw new com.zawed.api.platform.web.ApiException(org.springframework.http.HttpStatus.BAD_REQUEST,
						"SCOPE_MISMATCH", ex.getMessage(), "scope", null);
			}
		}

	}

	record CreateUserRequest(@NotBlank @Size(max = 254) String email, @NotBlank @Size(max = 200) String fullName,
			@NotNull Role role, @NotNull @Valid ScopeBody scope) {
	}

	record StatusRequest(@NotNull UserStatus status, @NotBlank @Size(max = 500) String reason) {
	}

	record ReasonRequest(@NotBlank @Size(max = 500) String reason) {
	}

	// ---------------------------------------------------------------------------------- responses

	@JsonInclude(JsonInclude.Include.NON_NULL)
	record ScopeResponse(@Schema(requiredMode = RequiredMode.REQUIRED) ScopeType type, UUID id) {

		static ScopeResponse of(Scope scope) {
			return new ScopeResponse(scope.type(), scope.id());
		}

	}

	record UserResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String email,
			@Schema(requiredMode = RequiredMode.REQUIRED) String fullName,
			@Schema(requiredMode = RequiredMode.REQUIRED) Role role,
			@Schema(requiredMode = RequiredMode.REQUIRED) ScopeResponse scope,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean mustChangePassword,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean mfaEnabled) {

		static UserResponse of(UserView user) {
			return new UserResponse(user.id(), user.email(), user.fullName(), user.role(), ScopeResponse.of(user.scope()),
					user.mustChangePassword(), user.mfaEnabled());
		}

	}

	/** The admin list adds status and lock state. */
	@JsonInclude(JsonInclude.Include.NON_NULL)
	record AdminUserResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String email,
			@Schema(requiredMode = RequiredMode.REQUIRED) String fullName,
			@Schema(requiredMode = RequiredMode.REQUIRED) Role role,
			@Schema(requiredMode = RequiredMode.REQUIRED) ScopeResponse scope,
			@Schema(requiredMode = RequiredMode.REQUIRED) UserStatus status,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean mustChangePassword,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean mfaEnabled, Instant lockedUntil,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static AdminUserResponse of(UserView user) {
			return new AdminUserResponse(user.id(), user.email(), user.fullName(), user.role(),
					ScopeResponse.of(user.scope()), user.status(), user.mustChangePassword(), user.mfaEnabled(),
					user.lockedUntil(), user.createdAt());
		}

	}

	record SessionResponse(@Schema(requiredMode = RequiredMode.REQUIRED) String accessToken,
			@Schema(requiredMode = RequiredMode.REQUIRED) long expiresInSeconds,
			@Schema(requiredMode = RequiredMode.REQUIRED) UserResponse user) {
	}

	enum LoginStatus {

		AUTHENTICATED, MFA_REQUIRED, MFA_ENROLLMENT_REQUIRED

	}

	@JsonInclude(JsonInclude.Include.NON_NULL)
	@Schema(description = "A session, or a challenge for the second sign-in step")
	record LoginResponse(@Schema(requiredMode = RequiredMode.REQUIRED) LoginStatus status, SessionResponse session,
			String challengeToken, Long expiresInSeconds) {
	}

	record EnrollmentResponse(@Schema(requiredMode = RequiredMode.REQUIRED) String secret,
			@Schema(requiredMode = RequiredMode.REQUIRED) String otpauthUri) {
	}

	record CreatedUserResponse(@Schema(requiredMode = RequiredMode.REQUIRED) AdminUserResponse user,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Shown once; never retrievable again") String temporaryPassword) {
	}

	record TemporaryPasswordResponse(@Schema(requiredMode = RequiredMode.REQUIRED,
			description = "Shown once; never retrievable again") String temporaryPassword) {
	}

	record SessionSummaryResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean current,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant lastSeenAt, String userAgent, String ipPrefix) {
	}

}
