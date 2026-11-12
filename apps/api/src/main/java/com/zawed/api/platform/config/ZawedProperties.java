package com.zawed.api.platform.config;

import java.util.List;

import jakarta.validation.Valid;
import jakarta.validation.constraints.AssertTrue;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotEmpty;
import jakarta.validation.constraints.NotNull;

import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.validation.annotation.Validated;

/**
 * ZAWED's own configuration, checked once at boot. A deploy with unsafe values refuses to start
 * instead of running quietly misconfigured. Secrets join this record with the features that need
 * them (the token keys arrive with authentication).
 */
@ConfigurationProperties("zawed")
@Validated
public record ZawedProperties(@NotNull Environment environment, @NotNull @Valid Cors cors) {

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

	private static boolean isLoopback(String origin) {
		return origin.contains("://localhost") || origin.contains("://127.") || origin.contains("://[::1]");
	}

}
