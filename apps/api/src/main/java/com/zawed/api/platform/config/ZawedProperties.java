package com.zawed.api.platform.config;

import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.List;
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

	/** A bootstrap token is a one-time password for the first Super Admin; it must not be guessable. */
	static final int MIN_BOOTSTRAP_TOKEN_CHARS = 32;

	/**
	 * The development values committed in application.yaml. They are public, so a deployed instance
	 * that carries any of them refuses to start.
	 */
	static final Set<String> COMMITTED_LOCAL_VALUES = Set.of("local-only-jwt-secret-do-not-deploy-0123456789abcdef",
			"bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=", "local-only-bootstrap-token-do-not-deploy");

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
	 * @param dataKey base64 of a 32-byte AES key that encrypts secrets at rest (TOTP secrets, later tag UIDs)
	 * @param bootstrapToken optional; enables the one-time creation of the first Super Admin
	 * @param cookieSecure whether session cookies carry the Secure attribute; always true when deployed
	 */
	public record Security(@NotBlank String jwtSecret, @NotBlank String dataKey, String bootstrapToken,
			boolean cookieSecure) {

		public byte[] jwtSecretBytes() {
			return jwtSecret.getBytes(StandardCharsets.UTF_8);
		}

		public byte[] dataKeyBytes() {
			return Base64.getDecoder().decode(dataKey);
		}

		public boolean bootstrapEnabled() {
			return bootstrapToken != null && !bootstrapToken.isBlank();
		}

		@Override
		public String toString() {
			return "Security[secrets hidden, cookieSecure=" + cookieSecure + "]";
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

	@AssertTrue(message = "a deployed environment must set its own ZAWED_JWT_SECRET, ZAWED_DATA_KEY and "
			+ "ZAWED_BOOTSTRAP_TOKEN (the committed development values are refused), and send cookies Secure")
	public boolean isDeployedSecurityOwn() {
		if (environment == null || security == null || !environment.isDeployed()) {
			return true;
		}
		return security.cookieSecure() && !COMMITTED_LOCAL_VALUES.contains(security.jwtSecret())
				&& !COMMITTED_LOCAL_VALUES.contains(security.dataKey())
				&& (!security.bootstrapEnabled() || !COMMITTED_LOCAL_VALUES.contains(security.bootstrapToken()));
	}

	private static boolean isLoopback(String origin) {
		return origin.contains("://localhost") || origin.contains("://127.") || origin.contains("://[::1]");
	}

}
