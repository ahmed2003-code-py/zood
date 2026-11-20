package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Owner;

/**
 * The second factor switched off for testing (zawed-dev only, owner 2026-11-19; production refuses
 * it, see DeployedStartupTests): every role signs in with its password alone, enrolled or not.
 */
@TestPropertySource(properties = "zawed.security.mfa-required=false")
class SignInWithoutMfaTests extends IdentityTestSupport {

	@Test
	void opsWithoutAnAuthenticatorSignsInWithThePasswordAlone() throws Exception {
		SeededUser ops = seed(Role.OPS);
		Owner.update("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = ?", ops.id());

		assertThat(JsonPath.<String>read(post("/api/v1/auth/login", loginBody(ops.email(), PASSWORD)).body(), "$.status"))
			.isEqualTo("AUTHENTICATED");
	}

	@Test
	void anEnrolledSuperAdminSignsInAtTheOneSignInPathWithoutACode() throws Exception {
		SeededUser admin = seed(Role.SUPER_ADMIN);

		java.net.http.HttpResponse<String> login = post("/api/v1/auth/login", loginBody(admin.email(), PASSWORD));
		assertThat(JsonPath.<String>read(login.body(), "$.status")).as(login.body()).isEqualTo("AUTHENTICATED");
		assertThat(JsonPath.<String>read(login.body(), "$.session.user.role")).isEqualTo("SUPER_ADMIN");
		assertThat(setCookies(login)).anyMatch((c) -> c.startsWith("zawed_rt=") && c.contains("Path=/api/v1/auth"));
	}

	@Test
	void aStationManagerSignsInWithThePasswordAlone() throws Exception {
		SeededUser manager = seed(Role.STATION_MANAGER, Scope.operator(UUID.randomUUID()), false);

		assertThat(JsonPath.<String>read(post("/api/v1/auth/login", loginBody(manager.email(), PASSWORD)).body(), "$.status"))
			.isEqualTo("AUTHENTICATED");
	}

}
