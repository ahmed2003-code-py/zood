package com.zawed.api.platform.config;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.List;

import jakarta.validation.Validation;
import jakarta.validation.Validator;
import jakarta.validation.ValidatorFactory;

import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

import com.zawed.api.platform.config.ZawedProperties.Cors;
import com.zawed.api.platform.config.ZawedProperties.Environment;
import com.zawed.api.platform.config.ZawedProperties.Security;

/** The checks that make a misconfigured deploy refuse to start. */
class ZawedPropertiesTests {

	private static ValidatorFactory factory;
	private static Validator validator;

	@BeforeAll
	static void createValidator() {
		factory = Validation.buildDefaultValidatorFactory();
		validator = factory.getValidator();
	}

	@AfterAll
	static void closeValidator() {
		factory.close();
	}

	@Test
	void acceptsLocalOriginsOnADevelopmentMachine() {
		assertThat(violations(Environment.LOCAL, "http://localhost:5173")).isEmpty();
	}

	@Test
	void acceptsHttpsOriginsWhenDeployed() {
		assertThat(violations(Environment.PRODUCTION, "https://app.zawed.example")).isEmpty();
		assertThat(violations(Environment.DEV, "https://zawed-web-dev.up.railway.app")).isEmpty();
	}

	@Test
	void refusesLocalhostWhenDeployed() {
		assertThat(violations(Environment.PRODUCTION, "http://localhost:5173")).isNotEmpty();
		assertThat(violations(Environment.DEV, "https://localhost:5173")).isNotEmpty();
		assertThat(violations(Environment.STAGING, "https://127.0.0.1")).isNotEmpty();
	}

	@Test
	void refusesPlainHttpWhenDeployed() {
		assertThat(violations(Environment.PRODUCTION, "http://app.zawed.example")).isNotEmpty();
	}

	@Test
	void refusesAWildcardEverywhere() {
		assertThat(violations(Environment.LOCAL, "*")).isNotEmpty();
		assertThat(violations(Environment.PRODUCTION, "https://*.zawed.example")).isNotEmpty();
	}

	@Test
	void refusesMissingValues() {
		assertThat(validator.validate(new ZawedProperties(null, new Cors(List.of("http://localhost:5173")), STRONG)))
			.isNotEmpty();
		assertThat(validator.validate(new ZawedProperties(Environment.LOCAL, new Cors(List.of()), STRONG))).isNotEmpty();
		assertThat(validator.validate(new ZawedProperties(Environment.LOCAL, null, STRONG))).isNotEmpty();
		assertThat(validator.validate(new ZawedProperties(Environment.LOCAL, new Cors(List.of("http://localhost:5173")), null)))
			.isNotEmpty();
	}

	@Test
	void refusesAShortJwtSecret() {
		assertThat(security(Environment.LOCAL, new Security("too-short", DATA_KEY, null, false))).isNotEmpty();
	}

	@Test
	void refusesADataKeyThatIsNotThirtyTwoBytes() {
		assertThat(security(Environment.LOCAL, new Security(JWT, "bm90LTMyLWJ5dGVz", null, false))).isNotEmpty();
		assertThat(security(Environment.LOCAL, new Security(JWT, "not base64 at all!", null, false))).isNotEmpty();
	}

	@Test
	void refusesAShortBootstrapToken() {
		assertThat(security(Environment.LOCAL, new Security(JWT, DATA_KEY, "short-token", false))).isNotEmpty();
	}

	@Test
	void refusesTheCommittedDevelopmentSecretsWhenDeployed() {
		assertThat(security(Environment.DEV, new Security("local-only-jwt-secret-do-not-deploy-0123456789abcdef",
				DATA_KEY, null, true))).isNotEmpty();
		assertThat(security(Environment.DEV, new Security(JWT, "bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=", null,
				true))).isNotEmpty();
		assertThat(security(Environment.DEV, new Security(JWT, DATA_KEY, "local-only-bootstrap-token-do-not-deploy",
				true))).isNotEmpty();
	}

	@Test
	void refusesInsecureCookiesWhenDeployed() {
		assertThat(security(Environment.PRODUCTION, new Security(JWT, DATA_KEY, null, false))).isNotEmpty();
		assertThat(security(Environment.PRODUCTION, new Security(JWT, DATA_KEY, null, true))).isEmpty();
	}

	private static final String JWT = "a-real-looking-secret-for-tests-only-0123456789";
	private static final String DATA_KEY = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
	private static final Security STRONG = new Security(JWT, DATA_KEY, null, true);

	private static List<String> security(Environment environment, Security security) {
		return validator.validate(new ZawedProperties(environment, new Cors(List.of("https://web.zawed.example")), security))
			.stream()
			.map((violation) -> violation.getPropertyPath() + ": " + violation.getMessage())
			.toList();
	}

	private static List<String> violations(Environment environment, String... origins) {
		return validator.validate(new ZawedProperties(environment, new Cors(List.of(origins)), STRONG))
			.stream()
			.map((violation) -> violation.getPropertyPath() + ": " + violation.getMessage())
			.toList();
	}

}
