package com.zawed.api.platform.web;

import static org.assertj.core.api.Assertions.assertThat;

import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

/** Which raw paths {@link CanonicalPathFilter} lets through (B7 "path matching"). */
class CanonicalPathFilterTests {

	@ParameterizedTest
	@ValueSource(strings = { "/", "/error", "/api/v1/auth/login", "/api/v1/auth/login/",
			"/api/v1/receipts/0193a1b2-7c4d-7e5f-8a9b-0c1d2e3f4a5b", "/api/v1/swagger-ui/index.html",
			"/api/v1/swagger-ui/swagger-ui-bundle.js", "/api/v1/openapi.json/swagger-config",
			"/api/v1/fleet-imports/0193a1b2-7c4d-7e5f-8a9b-0c1d2e3f4a5b/errors.csv", "/api/v1/a_b~c...d" })
	void aCanonicalPathPasses(String path) {
		assertThat(CanonicalPathFilter.isCanonical(path)).isTrue();
	}

	@ParameterizedTest
	@ValueSource(strings = { "", "api/v1/auth/login", "/api/v1/auth/logi%6E", "/api/v1/auth/logi%6e", "/api/v1/auth%2Flogin",
			"/api/v1/auth/%2E/login", "/api/v1/auth/logi%256E", "/api/v1/auth/login;x=1", "/api/v1/auth;jsessionid=1/login",
			"/api/v1//auth/login", "//api/v1/auth/login", "/api/v1/auth/./login", "/api/v1/auth/../auth/login",
			"/api/v1/auth/login/.", "/api/v1/auth/login/..", "/api/v1/auth\\login", "/api/v1/auth/login ",
			"/api/v1/auth/log+in", "/api/v1/auth/logïn", "/api/v1/auth/login\u0000" })
	void anyOtherSpellingIsRefused(String path) {
		assertThat(CanonicalPathFilter.isCanonical(path)).isFalse();
	}


	@org.junit.jupiter.api.Test
	void aVeryLongPathIsAnsweredWithoutOverflowingTheStack() {
		String canonical = "/a".repeat(8000);
		String refused = canonical + "/%61";
		org.assertj.core.api.Assertions.assertThatCode(() -> {
			aCanonicalPathPasses(canonical);
			anyOtherSpellingIsRefused(refused);
		}).doesNotThrowAnyException();
	}
}
