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
	void productionDoesNotStartWithTheSecondFactorSwitchedOff() {
		Throwable failure = start("deployed", "--zawed.environment=production", "--zawed.security.bootstrap-token=",
				"--zawed.cors.allowed-origins=https://web.zawed.example", OWN_JWT, OWN_DATA_KEY, SECURE_COOKIES,
				"--zawed.security.mfa-required=false");

		assertThat(messages(failure)).anyMatch((message) -> message.contains("mfa-required is false in production"));
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

	private static final String NEW_DATA_KEY = "bmV3LWRhdGEta2V5LWZvci1yb3RhdGlvbi10ZXN0cyE=";

	private static final String OLD_DATA_KEY = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

	@Test
	void aDeployInTheMiddleOfADataKeyRotationStarts() {
		Throwable failure = start("deployed", "--zawed.environment=dev",
				"--zawed.cors.allowed-origins=https://web.zawed.example", OWN_JWT, SECURE_COOKIES,
				"--zawed.security.data-key=" + NEW_DATA_KEY, "--zawed.security.data-key-id=2",
				"--zawed.security.data-keys-previous=1:" + OLD_DATA_KEY, "--zawed.security.lookup-key=" + OLD_DATA_KEY);

		assertThat(failure).isNull();
	}

	@Test
	void aRotatedDeployThatForgetsItsLookupKeyDoesNotStart() {
		Throwable failure = start("deployed", "--zawed.environment=dev",
				"--zawed.cors.allowed-origins=https://web.zawed.example", OWN_JWT, SECURE_COOKIES,
				"--zawed.security.data-key=" + NEW_DATA_KEY, "--zawed.security.data-key-id=2",
				"--zawed.security.data-keys-previous=1:" + OLD_DATA_KEY);

		assertThat(messages(failure)).anyMatch((message) -> message.contains("ZAWED_LOOKUP_KEY"));
	}

	private static final String THIRD_DATA_KEY = "dGhpcmQtZGF0YS1rZXktZm9yLXJvdGF0aW9uLXRlc3Q=";

	private static final String FRESH_LOOKUP_KEY = "ZnJlc2hseS1nZW5lcmF0ZWQtbG9va3VwLWtleS0wMDE=";

	@Test
	void aDeployWhoseLookupKeyIsNotTheOneItsHashesWereMadeWithDoesNotStart() {
		// Key 1 already retired, so the configuration checks cannot tell: only the database can.
		String database = TestDatabase.newDatabase("zawed_lookup");
		String[] retired = { "--zawed.environment=dev", "--zawed.cors.allowed-origins=https://web.zawed.example",
				OWN_JWT, SECURE_COOKIES, "--zawed.security.data-key=" + THIRD_DATA_KEY, "--zawed.security.data-key-id=3",
				"--zawed.security.data-keys-previous=2:" + NEW_DATA_KEY };

		assertThat(startOn(database, "deployed", with(retired, "--zawed.security.lookup-key=" + OLD_DATA_KEY)))
			.as("the first start records the lookup key's fingerprint").isNull();
		Throwable fresh = startOn(database, "deployed", with(retired, "--zawed.security.lookup-key=" + FRESH_LOOKUP_KEY));
		assertThat(messages(fresh)).anyMatch((message) -> message.contains("ZAWED_LOOKUP_KEY is not the key this "
				+ "database's lookup hashes were made with"));
		assertThat(messages(fresh)).noneMatch((message) -> message.contains(FRESH_LOOKUP_KEY)
				|| message.contains(OLD_DATA_KEY));
		assertThat(startOn(database, "deployed", with(retired, "--zawed.security.lookup-key=" + OLD_DATA_KEY)))
			.as("the original lookup key still starts").isNull();
	}

	private static String[] with(String[] args, String extra) {
		List<String> all = new ArrayList<>(List.of(args));
		all.add(extra);
		return all.toArray(String[]::new);
	}

	/**
	 * This class's own database: it starts the application with other keys than every other test class,
	 * and the first start on a database records its lookup key (data_key_state).
	 */
	private static String database;

	private static synchronized String database() {
		if (database == null) {
			database = TestDatabase.newDatabase("zawed_deployed");
		}
		return database;
	}

	/** Starts the application and returns why it failed, or null when it started (and is closed again). */
	private static Throwable start(String profile, String... args) {
		return startOn(database(), profile, args);
	}

	private static Throwable startOn(String databaseUrl, String profile, String... args) {
		List<String> arguments = new ArrayList<>(List.of("--server.port=0",
				"--spring.datasource.url=" + databaseUrl,
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
