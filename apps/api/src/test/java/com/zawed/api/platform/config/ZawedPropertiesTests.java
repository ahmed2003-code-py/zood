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
		assertThat(validator.validate(new ZawedProperties(null, new Cors(List.of("http://localhost:5173")))))
			.isNotEmpty();
		assertThat(validator.validate(new ZawedProperties(Environment.LOCAL, new Cors(List.of())))).isNotEmpty();
		assertThat(validator.validate(new ZawedProperties(Environment.LOCAL, null))).isNotEmpty();
	}

	private static List<String> violations(Environment environment, String... origins) {
		return validator.validate(new ZawedProperties(environment, new Cors(List.of(origins))))
			.stream()
			.map((violation) -> violation.getPropertyPath() + ": " + violation.getMessage())
			.toList();
	}

}
