package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.util.List;
import java.util.UUID;

import org.junit.jupiter.api.Test;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;

/**
 * B7 "path matching": our own filters (the sign-in limit, the password-change gate, the POS public
 * paths) must see a request path exactly as Spring Security and Spring MVC route it. Spring matches
 * decoded path segments with their matrix parameters removed, so {@code /api/v1/auth/logi%6E} is
 * the sign-in endpoint to Spring while a raw string comparison sees an unknown path. Every path
 * whose raw form is not already its canonical form is therefore refused before any filter or
 * controller looks at it, and the filters match with Spring's own matcher.
 *
 * <p>Requests go over real HTTP: MockMvc would not show what the container and the firewall do.
 */
class PathMatchingTests extends IdentityTestSupport {

	/** The sign-in steps, each with one letter percent-encoded (an unreserved character: legal, and let through by the firewall). */
	private static final List<String> ENCODED_SIGN_IN = List.of("/api/v1/auth/logi%6E", "/api/v1/auth/%6Cogin",
			"/api/v1/auth/logi%6e", "/api/v1/%61uth/login", "/api/v1/auth/mfa/verif%79",
			"/api/v1/auth/mfa/enroll/star%74", "/api/v1/auth/mfa/enroll/confir%6D", "/api/v1/setup/super-admi%6E",
			"/api/v1/auth/refres%68");

	@Test
	void anEncodedSignInPathCannotSpendMoreThanTheSignInLimit() throws Exception {
		// The plain path's budget is spent first (10 per minute per address, contract §6).
		for (int i = 0; i < SIGN_IN_PER_MINUTE; i++) {
			assertThat(code(post("/api/v1/auth/login", loginBody("probe-" + i + "@zawed.test", "whatever password"))))
				.isEqualTo("INVALID_CREDENTIALS");
		}
		assertThat(code(post("/api/v1/auth/login", loginBody("probe-x@zawed.test", "whatever password"))))
			.isEqualTo("RATE_LIMITED");

		// The same endpoint under another spelling must not open a fresh, unlimited door.
		for (int i = 0; i < 2 * SIGN_IN_PER_MINUTE; i++) {
			HttpResponse<String> encoded = post("/api/v1/auth/logi%6E",
					loginBody("spray-" + i + "@zawed.test", "whatever password"));
			assertThat(encoded.statusCode()).as("attempt %d: %s", i, encoded.body()).isEqualTo(400);
			assertThat(code(encoded)).isEqualTo("PATH_NOT_CANONICAL");
		}
	}

	@Test
	void noEncodedSpellingOfASignInStepReachesItsController() throws Exception {
		for (String path : ENCODED_SIGN_IN) {
			limiter.reset();
			HttpResponse<String> response = post(path, loginBody("probe@zawed.test", "whatever password"));
			assertThat(response.statusCode()).as("%s: %s", path, response.body()).isEqualTo(400);
			assertThat(code(response)).as(path).isEqualTo("PATH_NOT_CANONICAL");
		}
	}

	@Test
	void theRefusalIsTheStandardEnvelopeWithTheRequestId() throws Exception {
		HttpResponse<String> response = send(request("/api/v1/auth/logi%6E").header("X-Request-Id", "path-probe-1")
			.header("Content-Type", "application/json")
			.POST(HttpRequest.BodyPublishers.ofString(loginBody("probe@zawed.test", "whatever"))));

		assertThat(response.statusCode()).isEqualTo(400);
		assertThat(response.headers().firstValue("Content-Type")).hasValueSatisfying((type) -> assertThat(type)
			.startsWith("application/json"));
		assertThat(response.body()).contains("\"code\":\"PATH_NOT_CANONICAL\"", "\"requestId\":\"path-probe-1\"",
				"\"retryable\":false");
	}

	@Test
	void thePasswordChangeGateCannotBeWalkedAroundWithAnEncodedPath() throws Exception {
		SeededUser user = seed(Role.FLEET_MANAGER, new Scope(Role.FLEET_MANAGER.scopeType(), UUID.randomUUID()), true);
		String token = signIn(user).accessToken();

		// The gate as designed: me is allowed, anything else is refused until the password changes.
		assertThat(authed("GET", "/api/v1/auth/me", token, null).statusCode()).isEqualTo(200);
		HttpResponse<String> sessions = authed("GET", "/api/v1/auth/sessions", token, null);
		assertThat(code(sessions)).isEqualTo("PASSWORD_CHANGE_REQUIRED");

		for (String path : List.of("/api/v1/auth/session%73", "/api/v1/auth/m%65", "/api/v1/%61uth/sessions")) {
			HttpResponse<String> response = authed("GET", path, token, null);
			assertThat(response.statusCode()).as("%s: %s", path, response.body()).isEqualTo(400);
			assertThat(code(response)).as(path).isEqualTo("PATH_NOT_CANONICAL");
		}
	}

	@Test
	void anEncodedPosPathNeitherSkipsTheDeviceCheckNorLeavesThePosChain() throws Exception {
		// The two credential-obtaining POS calls are public by their exact path only.
		HttpResponse<String> activate = post("/api/v1/pos/activat%65",
				"{\"deviceCode\":\"X\",\"activationCode\":\"Y\",\"publicKey\":\"Z\"}");
		assertThat(activate.statusCode()).as(activate.body()).isEqualTo(400);
		assertThat(code(activate)).isEqualTo("PATH_NOT_CANONICAL");

		// A user token must never reach a POS controller, however the path is spelled.
		String token = signIn(seed(Role.FLEET_MANAGER)).accessToken();
		assertThat(authed("GET", "/api/v1/pos/snapshot", token, null).statusCode()).isEqualTo(401);
		for (String path : List.of("/api/v1/po%73/snapshot", "/api/v1/%70os/snapshot")) {
			HttpResponse<String> response = authed("GET", path, token, null);
			assertThat(response.statusCode()).as("%s: %s", path, response.body()).isEqualTo(400);
			assertThat(code(response)).as(path).isEqualTo("PATH_NOT_CANONICAL");
		}
	}

	@Test
	void anEncodedPublicPathIsRefusedLikeAnyOther() throws Exception {
		assertThat(get("/api/v1/health").statusCode()).isEqualTo(200);
		HttpResponse<String> response = get("/api/v1/healt%68");
		assertThat(response.statusCode()).as(response.body()).isEqualTo(400);
		assertThat(code(response)).isEqualTo("PATH_NOT_CANONICAL");
	}

	@Test
	void otherNonCanonicalSpellingsOfTheSignInPathAreRefused() throws Exception {
		// Matrix parameters, empty, dot and dot-dot segments, an encoded slash, dot or percent, a
		// backslash. Some are refused by the container or the firewall before our filter runs; the
		// rule is that none of them reaches the sign-in step.
		List<String> paths = List.of("/api/v1/auth/login;x=1", "/api/v1/auth;x=1/login", "/api/v1//auth/login",
				"/api/v1/auth/./login", "/api/v1/x/../auth/login", "/api/v1/auth%2Flogin", "/api/v1/auth/%2E/login",
				"/api/v1/auth/logi%256E", "/api/v1/auth%5Clogin");
		for (String path : paths) {
			limiter.reset();
			HttpResponse<String> response = post(path, loginBody("probe@zawed.test", "whatever password"));
			assertThat(response.statusCode()).as("%s: %s", path, response.body()).isEqualTo(400);
		}
	}

	@Test
	void aTrailingSlashOrAnotherCaseIsADifferentPathEverywhere() throws Exception {
		// Plain characters: Spring Security and Spring MVC both treat these as other paths (no trailing
		// slash match, case-sensitive), so they are not the public sign-in step and a caller without a
		// token is refused as for any protected route.
		for (String path : List.of("/api/v1/auth/login/", "/api/v1/auth/LOGIN", "/api/v1/Auth/login")) {
			HttpResponse<String> response = post(path, loginBody("probe@zawed.test", "whatever password"));
			assertThat(response.statusCode()).as("%s: %s", path, response.body()).isEqualTo(401);
			assertThat(code(response)).as(path).isEqualTo("UNAUTHENTICATED");
		}
		// With a token they reach no controller either.
		String token = signIn(seed(Role.FLEET_MANAGER)).accessToken();
		for (String path : List.of("/api/v1/auth/login/", "/api/v1/auth/LOGIN")) {
			HttpResponse<String> response = authed("POST", path, token, loginBody("probe@zawed.test", "whatever"));
			assertThat(response.statusCode()).as("%s: %s", path, response.body()).isEqualTo(404);
		}
	}

	@Test
	void aQueryStringMayStillCarryEncodedCharacters() throws Exception {
		// Only the path must be canonical: query values (a receipt signature, a search) are decoded as usual.
		HttpResponse<String> receipt = get("/api/v1/receipts/" + UUID.randomUUID() + "?sig=a%2Bb%3D");
		assertThat(receipt.statusCode()).as(receipt.body()).isEqualTo(404);
	}

	private static final int SIGN_IN_PER_MINUTE = 10;

}
