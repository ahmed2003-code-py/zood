package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.Map;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Owner;

/** Contract §3–§5: sign-in, lockout, doors, MFA, and what the caller can and cannot learn. */
class SignInTests extends IdentityTestSupport {

	@Test
	void aFleetManagerSignsInAndGetsAStrictHttpOnlyRefreshCookie() throws Exception {
		SeededUser user = seed(Role.FLEET_MANAGER);

		HttpResponse<String> response = post("/api/v1/auth/login", loginBody(user.email(), PASSWORD));

		assertThat(response.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(response.body(), "$.status")).isEqualTo("AUTHENTICATED");
		assertThat(JsonPath.<String>read(response.body(), "$.session.user.role")).isEqualTo("FLEET_MANAGER");
		assertThat(JsonPath.<String>read(response.body(), "$.session.user.scope.type")).isEqualTo("COMPANY");
		String refreshCookie = setCookies(response).stream().filter((c) -> c.startsWith("zawed_rt=")).findFirst().orElseThrow();
		assertThat(refreshCookie).contains("HttpOnly", "SameSite=Strict", "Path=/api/v1/auth");
		assertThat(setCookies(response)).anyMatch((c) -> c.startsWith("zawed_session=1") && !c.contains("HttpOnly"));

		String token = JsonPath.read(response.body(), "$.session.accessToken");
		HttpResponse<String> me = authed("GET", "/api/v1/auth/me", token, null);
		assertThat(me.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(me.body(), "$.email")).isEqualTo(user.email());
	}

	@Test
	void aWrongPasswordAndAnUnknownEmailLookExactlyTheSame() throws Exception {
		SeededUser user = seed(Role.ACCOUNTANT);

		HttpResponse<String> wrongPassword = post("/api/v1/auth/login", loginBody(user.email(), "not the password at all"));
		HttpResponse<String> unknownEmail = post("/api/v1/auth/login", loginBody("nobody-" + UUID.randomUUID() + "@zawed.test", PASSWORD));

		assertThat(wrongPassword.statusCode()).isEqualTo(401).isEqualTo(unknownEmail.statusCode());
		assertThat(withoutRequestId(wrongPassword.body())).isEqualTo(withoutRequestId(unknownEmail.body()));
		assertThat(code(wrongPassword)).isEqualTo("INVALID_CREDENTIALS");
	}

	@Test
	void fiveWrongPasswordsLockTheAccountEvenAgainstTheRightOneUntilOpsUnlocksIt() throws Exception {
		SeededUser user = seed(Role.STATION_MANAGER);
		for (int i = 0; i < 4; i++) {
			assertThat(code(post("/api/v1/auth/login", loginBody(user.email(), "wrong password " + i)))).isEqualTo("INVALID_CREDENTIALS");
		}
		HttpResponse<String> fifth = post("/api/v1/auth/login", loginBody(user.email(), "wrong password 5"));
		assertThat(fifth.statusCode()).isEqualTo(423);
		assertThat(code(fifth)).isEqualTo("ACCOUNT_LOCKED");

		HttpResponse<String> rightPassword = post("/api/v1/auth/login", loginBody(user.email(), PASSWORD));
		assertThat(code(rightPassword)).isEqualTo("ACCOUNT_LOCKED");
		assertThat(JsonPath.<Integer>read(rightPassword.body(), "$.params.retryAfterSeconds")).isPositive();

		SignedIn ops = signIn(seed(Role.OPS));
		assertThat(authed("POST", "/api/v1/admin/users/" + user.id() + "/unlock", ops.accessToken(), null).statusCode())
			.isEqualTo(204);
		assertThat(post("/api/v1/auth/login", loginBody(user.email(), PASSWORD)).statusCode()).isEqualTo(200);
	}

	@Test
	void aSuspendedAccountIsRevealedOnlyAfterTheRightPassword() throws Exception {
		SeededUser user = seed(Role.FLEET_MANAGER);
		Owner.update("update users set status = 'SUSPENDED' where id = ?", user.id());

		assertThat(code(post("/api/v1/auth/login", loginBody(user.email(), "a wrong password here")))).isEqualTo("INVALID_CREDENTIALS");
		HttpResponse<String> right = post("/api/v1/auth/login", loginBody(user.email(), PASSWORD));
		assertThat(right.statusCode()).isEqualTo(403);
		assertThat(code(right)).isEqualTo("ACCOUNT_DISABLED");
	}

	@Test
	void eachRoleUsesItsOwnDoor() throws Exception {
		SeededUser admin = seed(Role.SUPER_ADMIN);
		SeededUser ops = seed(Role.OPS);

		assertThat(code(post("/api/v1/auth/login", loginBody(admin.email(), PASSWORD)))).isEqualTo("INVALID_CREDENTIALS");
		assertThat(code(post("/api/v1/admin/auth/login", loginBody(ops.email(), PASSWORD)))).isEqualTo("INVALID_CREDENTIALS");
		assertThat(JsonPath.<String>read(post("/api/v1/admin/auth/login", loginBody(admin.email(), PASSWORD)).body(), "$.status"))
			.isEqualTo("MFA_REQUIRED");
	}

	@Test
	void opsMustEnrolASecondFactorOnFirstSignInAndCannotReplayACode() throws Exception {
		SeededUser ops = seed(Role.OPS);
		Owner.update("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = ?", ops.id());

		HttpResponse<String> login = post("/api/v1/auth/login", loginBody(ops.email(), PASSWORD));
		assertThat(JsonPath.<String>read(login.body(), "$.status")).isEqualTo("MFA_ENROLLMENT_REQUIRED");
		String challenge = JsonPath.read(login.body(), "$.challengeToken");

		HttpResponse<String> start = post("/api/v1/auth/mfa/enroll/start", "{\"challengeToken\":\"" + challenge + "\"}");
		assertThat(start.statusCode()).isEqualTo(200);
		String secretBase32 = JsonPath.read(start.body(), "$.secret");
		assertThat(JsonPath.<String>read(start.body(), "$.otpauthUri")).startsWith("otpauth://totp/ZAWED");
		// Asking again shows the same key: a phone that already scanned it keeps working.
		assertThat(JsonPath.<String>read(post("/api/v1/auth/mfa/enroll/start", "{\"challengeToken\":\"" + challenge + "\"}").body(),
				"$.secret")).isEqualTo(secretBase32);

		byte[] secret = base32Decode(secretBase32);
		String code = code(secret, 0);
		HttpResponse<String> confirm = post("/api/v1/auth/mfa/enroll/confirm",
				"{\"challengeToken\":\"" + challenge + "\",\"code\":\"" + code + "\"}");
		assertThat(confirm.statusCode()).as(confirm.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(confirm.body(), "$.status")).isEqualTo("AUTHENTICATED");
		assertThat(JsonPath.<Boolean>read(confirm.body(), "$.session.user.mfaEnabled")).isTrue();

		// The same code again, on a new sign-in: refused, because its time step was already used.
		HttpResponse<String> again = post("/api/v1/auth/login", loginBody(ops.email(), PASSWORD));
		String next = JsonPath.read(again.body(), "$.challengeToken");
		HttpResponse<String> replay = post("/api/v1/auth/mfa/verify", "{\"challengeToken\":\"" + next + "\",\"code\":\"" + code + "\"}");
		assertThat(code(replay)).isEqualTo("MFA_INVALID_CODE");
	}

	@Test
	void aChallengeDiesAfterFiveWrongCodes() throws Exception {
		SeededUser ops = seed(Role.OPS);
		String challenge = JsonPath.read(post("/api/v1/auth/login", loginBody(ops.email(), PASSWORD)).body(), "$.challengeToken");
		for (int i = 0; i < 5; i++) {
			assertThat(code(post("/api/v1/auth/mfa/verify", "{\"challengeToken\":\"" + challenge + "\",\"code\":\"000000\"}")))
				.isIn("MFA_INVALID_CODE");
		}
		HttpResponse<String> right = post("/api/v1/auth/mfa/verify",
				"{\"challengeToken\":\"" + challenge + "\",\"code\":\"" + code(ops.totpSecret(), 0) + "\"}");
		assertThat(code(right)).isEqualTo("MFA_CHALLENGE_EXPIRED");
		assertThat((Long) Owner.queryOne("select count(*) from audit_log where entity_id = ? and action = 'MFA_FAILED'", ops.id()))
			.isEqualTo(5L);
	}

	@Test
	void signInIsRateLimitedPerAddress() throws Exception {
		HttpResponse<String> last = null;
		for (int i = 0; i < 11; i++) {
			last = post("/api/v1/auth/login", loginBody("probe-" + i + "@zawed.test", "whatever password"));
		}
		assertThat(last.statusCode()).isEqualTo(429);
		assertThat(code(last)).isEqualTo("RATE_LIMITED");
		assertThat(last.headers().firstValue("Retry-After")).isPresent();
		assertThat(JsonPath.<Integer>read(last.body(), "$.retryAfterSeconds")).isPositive();
	}

	@Test
	void aBearerTokenDoesNotLiftTheSignInLimit() throws Exception {
		// B1 review finding 2: the per-user limit (300/min) must not replace the sign-in one (10/min).
		SignedIn someone = signIn(seed(Role.FLEET_MANAGER));
		limiter.reset();
		HttpResponse<String> last = null;
		for (int i = 0; i < 11; i++) {
			last = authed("POST", "/api/v1/auth/login", someone.accessToken(),
					loginBody("probe-" + i + "@zawed.test", "whatever password"));
		}
		assertThat(last.statusCode()).isEqualTo(429);
		assertThat(code(last)).isEqualTo("RATE_LIMITED");
	}

	@Test
	void refreshIsLimitedPerSessionNotPerAddress() throws Exception {
		// B1 review finding 3: one busy session must not lock out others behind the same address.
		SignedIn busy = signIn(seed(Role.ACCOUNTANT));
		SignedIn other = signIn(seed(Role.ACCOUNTANT));
		limiter.reset();
		// A stale cookie value keeps one bucket busy without rotating anything.
		String stale = busy.refreshToken() + "-stale";
		for (int i = 0; i < 30; i++) {
			withCookie("/api/v1/auth/refresh", AuthCookies.USER_COOKIE, stale);
		}
		HttpResponse<String> blocked = withCookie("/api/v1/auth/refresh", AuthCookies.USER_COOKIE, stale);
		assertThat(code(blocked)).isEqualTo("RATE_LIMITED");
		HttpResponse<String> fine = withCookie("/api/v1/auth/refresh", AuthCookies.USER_COOKIE, other.refreshToken());
		assertThat(fine.statusCode()).as(fine.body()).isEqualTo(200);
	}

	@Test
	void anOldEnrolmentChallengeCannotReplaceAnAuthenticator() throws Exception {
		// B1 review finding 5: the first completed enrolment wins; a second challenge cannot overwrite it.
		SeededUser ops = seed(Role.OPS);
		Owner.update("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = ?", ops.id());
		String first = JsonPath.read(post("/api/v1/auth/login", loginBody(ops.email(), PASSWORD)).body(),
				"$.challengeToken");
		String second = JsonPath.read(post("/api/v1/auth/login", loginBody(ops.email(), PASSWORD)).body(),
				"$.challengeToken");
		byte[] firstSecret = base32Decode(JsonPath.read(
				post("/api/v1/auth/mfa/enroll/start", "{\"challengeToken\":\"" + first + "\"}").body(), "$.secret"));
		byte[] secondSecret = base32Decode(JsonPath.read(
				post("/api/v1/auth/mfa/enroll/start", "{\"challengeToken\":\"" + second + "\"}").body(), "$.secret"));

		assertThat(post("/api/v1/auth/mfa/enroll/confirm",
				"{\"challengeToken\":\"" + first + "\",\"code\":\"" + code(firstSecret, 0) + "\"}").statusCode())
			.isEqualTo(200);
		HttpResponse<String> late = post("/api/v1/auth/mfa/enroll/confirm",
				"{\"challengeToken\":\"" + second + "\",\"code\":\"" + code(secondSecret, 0) + "\"}");
		assertThat(code(late)).isEqualTo("MFA_CHALLENGE_EXPIRED");
	}

	@Test
	void aPasswordResetVoidsOpenChallenges() throws Exception {
		SeededUser target = seed(Role.OPS);
		String challenge = JsonPath.read(post("/api/v1/auth/login", loginBody(target.email(), PASSWORD)).body(),
				"$.challengeToken");
		SignedIn admin = signIn(seed(Role.SUPER_ADMIN));
		assertThat(authed("POST", "/api/v1/admin/users/" + target.id() + "/reset-password", admin.accessToken(), null)
			.statusCode()).isEqualTo(200);
		HttpResponse<String> verify = post("/api/v1/auth/mfa/verify",
				"{\"challengeToken\":\"" + challenge + "\",\"code\":\"" + code(target.totpSecret(), 0) + "\"}");
		assertThat(code(verify)).isEqualTo("MFA_CHALLENGE_EXPIRED");
	}

	@Test
	void aScopedUserCanSignInWithoutASecondFactor() throws Exception {
		SeededUser user = seed(Role.STATION_MANAGER, Scope.operator(UUID.randomUUID()), false);
		assertThat(JsonPath.<String>read(post("/api/v1/auth/login", loginBody(user.email().toUpperCase(), PASSWORD)).body(),
				"$.status")).isEqualTo("AUTHENTICATED");
	}

	private static String withoutRequestId(String body) {
		Map<String, Object> map = JsonPath.read(body, "$");
		map.remove("requestId");
		return map.toString();
	}

	private static byte[] base32Decode(String value) {
		String alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
		byte[] out = new byte[value.length() * 5 / 8];
		int buffer = 0;
		int bits = 0;
		int index = 0;
		for (char c : value.toCharArray()) {
			buffer = (buffer << 5) | alphabet.indexOf(c);
			bits += 5;
			if (bits >= 8) {
				out[index++] = (byte) (buffer >> (bits - 8));
				bits -= 8;
			}
		}
		return out;
	}

}
