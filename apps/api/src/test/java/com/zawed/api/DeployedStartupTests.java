package com.zawed.api;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.catchThrowable;

import java.util.ArrayList;
import java.util.List;

import org.junit.jupiter.api.Test;
import org.springframework.boot.builder.SpringApplicationBuilder;
import org.springframework.context.ConfigurableApplicationContext;

import com.zawed.api.support.TestDatabase;

/**
 * An internet-facing instance must refuse to start when it is configured like a development
 * machine. Each case boots the real application with the real configuration files, so what is
 * tested is what a deploy does, not only the validator.
 */
class DeployedStartupTests {

	private static final String OWN_JWT = "--zawed.security.jwt-secret=a-deploy-specific-secret-for-this-test-0123456789";

	private static final String OWN_DATA_KEY = "--zawed.security.data-key=MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

	private static final String SECURE_COOKIES = "--zawed.security.cookie-secure=true";

	@Test
	void aDeployThatForgetsItsEnvironmentDoesNotStart() {
		Throwable failure = start("deployed");

		assertThat(messages(failure)).anyMatch((message) -> message.contains("zawed.environment"));
	}

	@Test
	void aDeployConfiguredAsLocalDoesNotStart() {
		Throwable failure = start("deployed", "--zawed.environment=local",
				"--zawed.cors.allowed-origins=https://web.zawed.example", OWN_JWT, OWN_DATA_KEY, SECURE_COOKIES);

		assertThat(messages(failure)).anyMatch((message) -> message.contains("Set ZAWED_ENVIRONMENT"));
	}

	@Test
	void aDeployWithLocalhostOriginsDoesNotStart() {
		Throwable failure = start("deployed", "--zawed.environment=dev",
				"--zawed.cors.allowed-origins=http://localhost:5173", OWN_JWT, OWN_DATA_KEY, SECURE_COOKIES);

		assertThat(messages(failure)).anyMatch((message) -> message.contains("allowed-origins"));
	}

	@Test
	void aDeployedEnvironmentWithoutTheProfileDoesNotStart() {
		Throwable failure = start(null, "--zawed.environment=production", "--zawed.security.bootstrap-token=",
				"--zawed.cors.allowed-origins=https://web.zawed.example", OWN_JWT, OWN_DATA_KEY, SECURE_COOKIES);

		assertThat(messages(failure)).anyMatch((message) -> message.contains("SPRING_PROFILES_ACTIVE=deployed"));
	}

	@Test
	void aCorrectlyConfiguredDeployStarts() {
		Throwable failure = start("deployed", "--zawed.environment=dev",
				"--zawed.cors.allowed-origins=https://web.zawed.example", OWN_JWT, OWN_DATA_KEY, SECURE_COOKIES);

		assertThat(failure).isNull();
	}

	@Test
	void aDeployWithoutItsOwnSecretsDoesNotStart() {
		Throwable failure = start("deployed", "--zawed.environment=dev",
				"--zawed.cors.allowed-origins=https://web.zawed.example");

		assertThat(messages(failure)).anyMatch((message) -> message.contains("zawed.security"));
	}

	@Test
	void aDeployWithTheCommittedDevelopmentSecretDoesNotStart() {
		Throwable failure = start("deployed", "--zawed.environment=dev",
				"--zawed.cors.allowed-origins=https://web.zawed.example",
				"--zawed.security.jwt-secret=local-only-jwt-secret-do-not-deploy-0123456789abcdef", OWN_DATA_KEY);

		assertThat(messages(failure)).anyMatch((message) -> message.contains("committed development values"));
	}

	/** Starts the application and returns why it failed, or null when it started (and is closed again). */
	private static Throwable start(String profile, String... args) {
		List<String> arguments = new ArrayList<>(List.of("--server.port=0",
				"--spring.datasource.url=" + TestDatabase.jdbcUrl(),
				"--spring.datasource.username=" + TestDatabase.USERNAME,
				"--spring.datasource.password=" + TestDatabase.PASSWORD));
		arguments.addAll(List.of(args));
		SpringApplicationBuilder builder = new SpringApplicationBuilder(ZawedApiApplication.class);
		if (profile != null) {
			builder.profiles(profile);
		}
		return catchThrowable(() -> {
			try (ConfigurableApplicationContext context = builder.run(arguments.toArray(String[]::new))) {
				assertThat(context.isActive()).isTrue();
			}
		});
	}

	private static List<String> messages(Throwable failure) {
		assertThat(failure).as("the application should have refused to start").isNotNull();
		List<String> messages = new ArrayList<>();
		for (Throwable cause = failure; cause != null; cause = cause.getCause()) {
			messages.add(String.valueOf(cause.getMessage()));
		}
		return messages;
	}

}
