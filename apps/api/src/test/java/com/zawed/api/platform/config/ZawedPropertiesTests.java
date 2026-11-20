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
		assertThat(security(Environment.LOCAL, legacy("too-short", DATA_KEY, null, false))).isNotEmpty();
	}

	@Test
	void refusesADataKeyThatIsNotThirtyTwoBytes() {
		assertThat(security(Environment.LOCAL, legacy(JWT, "bm90LTMyLWJ5dGVz", null, false))).isNotEmpty();
		assertThat(security(Environment.LOCAL, legacy(JWT, "not base64 at all!", null, false))).isNotEmpty();
	}

	@Test
	void refusesAShortBootstrapToken() {
		assertThat(security(Environment.LOCAL, legacy(JWT, DATA_KEY, "short-token", false))).isNotEmpty();
	}

	@Test
	void refusesTheCommittedDevelopmentSecretsWhenDeployed() {
		assertThat(security(Environment.DEV, legacy("local-only-jwt-secret-do-not-deploy-0123456789abcdef",
				DATA_KEY, null, true))).isNotEmpty();
		assertThat(security(Environment.DEV, legacy(JWT, "bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=", null,
				true))).isNotEmpty();
		assertThat(security(Environment.DEV, legacy(JWT, DATA_KEY, "local-only-bootstrap-token-do-not-deploy",
				true))).isNotEmpty();
	}

	@Test
	void refusesInsecureCookiesWhenDeployed() {
		assertThat(security(Environment.PRODUCTION, legacy(JWT, DATA_KEY, null, false))).isNotEmpty();
		assertThat(security(Environment.PRODUCTION, legacy(JWT, DATA_KEY, null, true))).isEmpty();
	}

	// ------------------------------------------------------------------- the data key ring (ADR 0027)

	@Test
	void aDeploymentThatSetsOnlyItsDataKeyIsKeyOneAsBefore() {
		Security legacy = legacy(JWT, DATA_KEY, null, true);

		assertThat(security(Environment.PRODUCTION, legacy)).isEmpty();
		assertThat(legacy.currentDataKeyId()).isEqualTo(1);
		assertThat(legacy.dataKeys()).containsOnlyKeys(1);
		assertThat(legacy.lookupKeyBytes()).isEqualTo(legacy.dataKeyBytes());
	}

	@Test
	void acceptsARotationWithTheOldKeyAsPreviousAndTheLookupKeyPinned() {
		Security rotated = ring(NEW_KEY, 2, "1:" + DATA_KEY, DATA_KEY);

		assertThat(security(Environment.PRODUCTION, rotated)).isEmpty();
		assertThat(rotated.currentDataKeyId()).isEqualTo(2);
		assertThat(rotated.dataKeys()).containsOnlyKeys(2, 1);
		assertThat(rotated.dataKeys().get(1)).isEqualTo(java.util.Base64.getDecoder().decode(DATA_KEY));
		assertThat(rotated.lookupKeyBytes()).isEqualTo(java.util.Base64.getDecoder().decode(DATA_KEY));
		// Spaces around the entries are tolerated; several previous keys are allowed.
		assertThat(security(Environment.PRODUCTION, ring(NEW_KEY, 3, " 1:" + DATA_KEY + " , 2:" + THIRD_KEY, DATA_KEY)))
			.isEmpty();
	}

	@Test
	void acceptsANewKeyStagedAsPreviousBeforeItBecomesCurrent() {
		// The first deploy of the recommended procedure: every instance can read key 2 before any writes it.
		assertThat(security(Environment.PRODUCTION, ring(DATA_KEY, 1, "2:" + NEW_KEY, DATA_KEY))).isEmpty();
		assertThat(security(Environment.PRODUCTION, ring(DATA_KEY, 1, "2:" + NEW_KEY, null))).isEmpty();
	}

	@Test
	void refusesAKeyIdOutsideOneByte() {
		assertThat(security(Environment.LOCAL, ring(NEW_KEY, 0, null, DATA_KEY))).anyMatch(m -> m.contains("1 to 255"));
		assertThat(security(Environment.LOCAL, ring(NEW_KEY, 256, null, DATA_KEY))).anyMatch(m -> m.contains("1 to 255"));
	}

	@Test
	void refusesMalformedPreviousKeys() {
		for (String previous : new String[] { "1=" + DATA_KEY, ":" + DATA_KEY, "x:" + DATA_KEY, "1:", "1:not base64!",
				"1:bm90LTMyLWJ5dGVz", "0:" + DATA_KEY, "256:" + DATA_KEY, "1:" + DATA_KEY + ",", DATA_KEY }) {
			assertThat(security(Environment.LOCAL, ring(NEW_KEY, 2, previous, DATA_KEY))).as(previous)
				.anyMatch(m -> m.contains("ZAWED_DATA_KEYS_PREVIOUS) must be"));
		}
	}

	@Test
	void refusesTheSameIdTwiceAmongThePreviousKeys() {
		assertThat(security(Environment.LOCAL, ring(NEW_KEY, 3, "1:" + DATA_KEY + ",1:" + THIRD_KEY, DATA_KEY)))
			.anyMatch(m -> m.contains("lists the same key id twice"));
	}

	@Test
	void refusesTheCurrentIdAlsoListedAsPrevious() {
		assertThat(security(Environment.LOCAL, ring(NEW_KEY, 2, "2:" + DATA_KEY, DATA_KEY)))
			.anyMatch(m -> m.contains("is also listed in data-keys-previous"));
	}

	@Test
	void refusesARotationThatForgotToGenerateANewKey() {
		assertThat(security(Environment.LOCAL, ring(DATA_KEY, 2, "1:" + DATA_KEY, THIRD_KEY)))
			.anyMatch(m -> m.contains("the same data key appears under two ids"));
		assertThat(security(Environment.LOCAL, ring(NEW_KEY, 3, "1:" + DATA_KEY + ",2:" + DATA_KEY, DATA_KEY)))
			.anyMatch(m -> m.contains("the same data key appears under two ids"));
	}

	@Test
	void refusesARotationWithoutThePinnedLookupKey() {
		// Without it the lookup hashes would follow the new key and no existing tag would be found.
		assertThat(security(Environment.LOCAL, ring(NEW_KEY, 2, "1:" + DATA_KEY, null)))
			.anyMatch(m -> m.contains("lookup-key (ZAWED_LOOKUP_KEY) must be set once"));
		assertThat(security(Environment.LOCAL, ring(NEW_KEY, 2, "1:" + DATA_KEY, NEW_KEY)))
			.anyMatch(m -> m.contains("lookup-key (ZAWED_LOOKUP_KEY) must be set once"));
	}

	@Test
	void refusesALookupKeyThatIsNotThirtyTwoBytes() {
		assertThat(security(Environment.LOCAL, ring(NEW_KEY, 2, "1:" + DATA_KEY, "bm90LTMyLWJ5dGVz")))
			.anyMatch(m -> m.contains("lookup-key (ZAWED_LOOKUP_KEY) must be the base64"));
	}

	@Test
	void refusesTheCommittedDevelopmentDataKeyAnywhereInTheRingWhenDeployed() {
		String committed = "bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=";
		assertThat(security(Environment.DEV, ring(NEW_KEY, 2, "1:" + committed, DATA_KEY)))
			.anyMatch(m -> m.contains("committed development values"));
		assertThat(security(Environment.DEV, ring(NEW_KEY, 2, "1:" + DATA_KEY, committed)))
			.anyMatch(m -> m.contains("committed development values"));
		// Written without its padding it is still the same key.
		assertThat(security(Environment.DEV, ring(NEW_KEY, 2, "1:" + committed.replace("=", ""), DATA_KEY)))
			.anyMatch(m -> m.contains("committed development values"));
		// On a development machine the committed key is the old key of a local rotation rehearsal.
		assertThat(security(Environment.LOCAL, ring(NEW_KEY, 2, "1:" + committed, committed))).isEmpty();
	}

	private static final String JWT = "a-real-looking-secret-for-tests-only-0123456789";
	private static final String DATA_KEY = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
	private static final String NEW_KEY = "bmV3LWRhdGEta2V5LWZvci1yb3RhdGlvbi10ZXN0cyE=";
	private static final String THIRD_KEY = "dGhpcmQtZGF0YS1rZXktZm9yLXJvdGF0aW9uLXRlc3Q=";
	private static final Security STRONG = legacy(JWT, DATA_KEY, null, true);

	/** The configuration of a deployment that sets only ZAWED_DATA_KEY. */
	private static Security legacy(String jwt, String dataKey, String bootstrapToken, boolean cookieSecure) {
		return new Security(jwt, dataKey, null, null, null, bootstrapToken, cookieSecure);
	}

	private static Security ring(String dataKey, Integer dataKeyId, String previous, String lookupKey) {
		return new Security(JWT, dataKey, dataKeyId, previous, lookupKey, null, true);
	}

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
