package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.EnumMap;
import java.util.EnumSet;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.UUID;

import org.assertj.core.api.SoftAssertions;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.beans.factory.annotation.Qualifier;
import org.springframework.web.servlet.mvc.method.annotation.RequestMappingHandlerMapping;

import com.zawed.api.platform.security.Role;

/**
 * Every endpoint × every role, against an explicit table written by hand (contract §4). The table
 * is not derived from the annotations, so a wrong annotation fails here instead of being echoed.
 * An endpoint missing from the table fails the first test: nothing ships without a decision.
 */
class AuthorizationMatrixTests extends IdentityTestSupport {

	private static final Set<Role> EVERYONE = EnumSet.allOf(Role.class);
	private static final Set<Role> PUBLIC = Set.of();

	/** Who may call it, and a body that passes validation (method security runs after validation). */
	private record Rule(Set<Role> allowed, String body) {

		boolean isPublic() {
			return allowed == PUBLIC;
		}

	}

	private static final Map<String, Rule> MATRIX = new LinkedHashMap<>();

	static {
		String login = "{\"email\":\"matrix@zawed.test\",\"password\":\"matrix password\"}";
		String challenge = "{\"challengeToken\":\"x\"}";
		String challengeCode = "{\"challengeToken\":\"x\",\"code\":\"123456\"}";
		MATRIX.put("GET /api/v1/meta", new Rule(PUBLIC, null));
		MATRIX.put("POST /api/v1/auth/login", new Rule(PUBLIC, login));
		MATRIX.put("POST /api/v1/auth/mfa/verify", new Rule(PUBLIC, challengeCode));
		MATRIX.put("POST /api/v1/auth/mfa/enroll/start", new Rule(PUBLIC, challenge));
		MATRIX.put("POST /api/v1/auth/mfa/enroll/confirm", new Rule(PUBLIC, challengeCode));
		MATRIX.put("POST /api/v1/auth/refresh", new Rule(PUBLIC, null));
		MATRIX.put("POST /api/v1/auth/logout", new Rule(PUBLIC, null));
		MATRIX.put("POST /api/v1/admin/auth/login", new Rule(PUBLIC, login));
		MATRIX.put("POST /api/v1/admin/auth/refresh", new Rule(PUBLIC, null));
		MATRIX.put("POST /api/v1/admin/auth/logout", new Rule(PUBLIC, null));
		MATRIX.put("POST /api/v1/setup/super-admin", new Rule(PUBLIC,
				"{\"setupToken\":\"x\",\"email\":\"s@zawed.test\",\"fullName\":\"S\",\"password\":\"a long setup password\"}"));
		MATRIX.put("GET /api/v1/auth/me", new Rule(EVERYONE, null));
		MATRIX.put("POST /api/v1/auth/password", new Rule(EVERYONE,
				"{\"currentPassword\":\"not the current one\",\"newPassword\":\"a new long password here\"}"));
		MATRIX.put("GET /api/v1/auth/sessions", new Rule(EVERYONE, null));
		MATRIX.put("DELETE /api/v1/auth/sessions/{id}", new Rule(EVERYONE, null));
		MATRIX.put("GET /api/v1/admin/users", new Rule(EnumSet.of(Role.SUPER_ADMIN, Role.OPS), null));
		MATRIX.put("POST /api/v1/admin/users", new Rule(EnumSet.of(Role.SUPER_ADMIN), null));
		MATRIX.put("POST /api/v1/admin/users/{id}/reset-password", new Rule(EnumSet.of(Role.SUPER_ADMIN, Role.OPS), null));
		MATRIX.put("POST /api/v1/admin/users/{id}/unlock", new Rule(EnumSet.of(Role.SUPER_ADMIN, Role.OPS), null));
		MATRIX.put("POST /api/v1/admin/users/{id}/status",
				new Rule(EnumSet.of(Role.SUPER_ADMIN), "{\"status\":\"SUSPENDED\",\"reason\":\"matrix\"}"));
		MATRIX.put("POST /api/v1/admin/users/{id}/mfa/reset", new Rule(EnumSet.of(Role.SUPER_ADMIN), "{\"reason\":\"matrix\"}"));
		MATRIX.put("GET /api/v1/admin/audit", new Rule(EnumSet.of(Role.SUPER_ADMIN), null));
	}

	@Autowired
	@Qualifier("requestMappingHandlerMapping")
	RequestMappingHandlerMapping mappings;

	@Test
	void everyEndpointHasADecision() {
		Set<String> registered = new TreeSet<>();
		mappings.getHandlerMethods().forEach((info, handler) -> {
			if (!handler.getBeanType().getPackageName().startsWith("com.zawed.api")) {
				return; // springdoc's own description endpoint, the error controller
			}
			for (String path : info.getPatternValues()) {
				if (!path.startsWith("/api/v1")) {
					continue;
				}
				info.getMethodsCondition().getMethods().forEach((method) -> registered.add(method.name() + " " + path));
			}
		});
		assertThat(MATRIX.keySet()).as("endpoints without a row in the authorization matrix")
			.containsAll(registered);
		assertThat(registered).as("rows for endpoints that no longer exist").containsAll(MATRIX.keySet());
	}

	@Test
	void eachRoleIsAllowedExactlyWhatTheMatrixSays() throws Exception {
		Map<Role, String> tokens = new EnumMap<>(Role.class);
		for (Role role : Role.values()) {
			tokens.put(role, signIn(seed(role)).accessToken());
		}

		SoftAssertions softly = new SoftAssertions();
		for (var entry : MATRIX.entrySet()) {
			String method = entry.getKey().substring(0, entry.getKey().indexOf(' '));
			String path = entry.getKey().substring(method.length() + 1).replace("{id}", UUID.randomUUID().toString());
			Rule rule = entry.getValue();
			String body = rule.body() != null ? rule.body() : createUserBody(entry.getKey());
			if (rule.isPublic()) {
				HttpResponse<String> anonymous = send(method, path, null, body);
				softly.assertThat(codeOrNone(anonymous)).as("anonymous %s", entry.getKey())
					.isNotIn("UNAUTHENTICATED", "FORBIDDEN");
				continue;
			}
			HttpResponse<String> anonymous = send(method, path, null, body);
			softly.assertThat(anonymous.statusCode()).as("anonymous %s", entry.getKey()).isEqualTo(401);
			for (Role role : Role.values()) {
				HttpResponse<String> response = send(method, path, tokens.get(role), body);
				if (rule.allowed().contains(role)) {
					softly.assertThat(codeOrNone(response)).as("%s %s (%s)", role, entry.getKey(), response.body())
						.isNotIn("UNAUTHENTICATED", "FORBIDDEN");
				}
				else {
					softly.assertThat(response.statusCode()).as("%s %s", role, entry.getKey()).isEqualTo(403);
					softly.assertThat(codeOrNone(response)).as("%s %s", role, entry.getKey()).isEqualTo("FORBIDDEN");
				}
			}
		}
		softly.assertAll();
	}

	private HttpResponse<String> send(String method, String path, String token, String body) throws Exception {
		if (token == null) {
			return "GET".equals(method) ? get(path) : body == null ? post(path, "{}") : post(path, body);
		}
		return authed(method, path, token, "GET".equals(method) || "DELETE".equals(method) ? null : body);
	}

	private static String createUserBody(String key) {
		if (!key.equals("POST /api/v1/admin/users")) {
			return null;
		}
		return "{\"email\":\"m-" + UUID.randomUUID() + "@zawed.test\",\"fullName\":\"M\",\"role\":\"ACCOUNTANT\","
				+ "\"scope\":{\"type\":\"COMPANY\",\"id\":\"" + UUID.randomUUID() + "\"}}";
	}

	private static String codeOrNone(HttpResponse<String> response) {
		String body = response.body();
		return body == null || !body.contains("\"code\"") ? "(none)" : code(response);
	}

}
