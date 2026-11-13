package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.support.Owner;

/**
 * The first Super Admin, created once. This class gets its own application context and empties the
 * Super Admin rows first, because "no Super Admin exists" is the precondition under test.
 */
@TestPropertySource(properties = "zawed.security.bootstrap-token=bootstrap-token-for-the-test-0123456789abcdef")
class BootstrapTests extends IdentityTestSupport {

	private static final String TOKEN = "bootstrap-token-for-the-test-0123456789abcdef";

	@Test
	void theFirstSuperAdminCanBeCreatedOnceAndThenTheDoorIsGone() throws Exception {
		Owner.update("update users set role = 'OPS' where role = 'SUPER_ADMIN'");

		HttpResponse<String> wrongToken = post("/api/v1/setup/super-admin", body("not-the-token"));
		assertThat(wrongToken.statusCode()).isEqualTo(404);
		assertThat(code(wrongToken)).isEqualTo("SETUP_NOT_AVAILABLE");

		HttpResponse<String> created = post("/api/v1/setup/super-admin", body(TOKEN));
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(created.body(), "$.role")).isEqualTo("SUPER_ADMIN");

		HttpResponse<String> again = post("/api/v1/setup/super-admin", body(TOKEN));
		assertThat(again.statusCode()).isEqualTo(404);
		assertThat((Long) Owner.queryOne("select count(*) from users where role = 'SUPER_ADMIN'")).isEqualTo(1L);
	}

	private static String body(String token) {
		return "{\"setupToken\":\"" + token + "\",\"email\":\"root-" + UUID.randomUUID()
				+ "@zawed.test\",\"fullName\":\"First Admin\",\"password\":\"a long first admin password\"}";
	}

}
