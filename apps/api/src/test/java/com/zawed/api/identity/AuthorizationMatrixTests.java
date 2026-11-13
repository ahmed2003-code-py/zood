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

		// ------------------------------------------------------------------ B2 master data
		Set<Role> platform = EnumSet.of(Role.SUPER_ADMIN, Role.OPS);
		Set<Role> superAdmin = EnumSet.of(Role.SUPER_ADMIN);
		Set<Role> company = EnumSet.of(Role.FLEET_MANAGER, Role.ACCOUNTANT);
		Set<Role> station = EnumSet.of(Role.STATION_MANAGER);
		String reason = "\"reason\":\"matrix\"";
		String names = "\"legalName\":\"M\",\"nameAr\":\"M\",\"nameEn\":\"M\"";
		String vehicle = "{\"plate\":\"MTX 1\",\"make\":\"M\",\"model\":\"M\",\"fuelType\":\"GAS_92\","
				+ "\"tankCapacityMl\":50000,\"tier\":\"NORMAL\"}";
		String stationBody = "{\"nameAr\":\"M\",\"nameEn\":\"M\",\"connectivity\":\"NORMAL\",\"fuelTypes\":[\"GAS_92\"]}";
		String unknown = "\"" + UUID.randomUUID() + "\"";

		MATRIX.put("GET /api/v1/ops/companies", new Rule(platform, null));
		MATRIX.put("POST /api/v1/ops/companies",
				new Rule(platform, "{\"company\":{" + names + "},\"lowBalanceThresholdPiastres\":0}"));
		MATRIX.put("GET /api/v1/ops/companies/{id}", new Rule(platform, null));
		MATRIX.put("PUT /api/v1/ops/companies/{id}", new Rule(platform, "{" + names + "}"));
		MATRIX.put("PUT /api/v1/ops/companies/{id}/policy",
				new Rule(superAdmin, "{\"cooldownMinutes\":180,\"lowBalanceThresholdPiastres\":0," + reason + "}"));
		MATRIX.put("POST /api/v1/ops/companies/{id}/status", new Rule(platform, "{\"status\":\"FROZEN\"," + reason + "}"));
		MATRIX.put("GET /api/v1/ops/companies/{id}/departments", new Rule(platform, null));
		MATRIX.put("POST /api/v1/ops/companies/{id}/departments", new Rule(platform, "{\"name\":\"Matrix\"}"));
		MATRIX.put("GET /api/v1/company/profile", new Rule(company, null));
		MATRIX.put("GET /api/v1/company/departments", new Rule(company, null));

		MATRIX.put("GET /api/v1/ops/vehicles", new Rule(platform, null));
		MATRIX.put("POST /api/v1/ops/vehicles", new Rule(platform, "{\"companyId\":" + unknown + ",\"vehicle\":" + vehicle + "}"));
		MATRIX.put("GET /api/v1/ops/vehicles/{id}", new Rule(platform, null));
		MATRIX.put("PUT /api/v1/ops/vehicles/{id}", new Rule(platform, vehicle));
		MATRIX.put("POST /api/v1/ops/vehicles/{id}/status", new Rule(platform, "{\"status\":\"SUSPENDED\"," + reason + "}"));
		MATRIX.put("POST /api/v1/ops/vehicles/{id}/tags", new Rule(platform, "{\"uid\":\"04A1B2C3\"}"));
		MATRIX.put("POST /api/v1/ops/tags/{id}/status", new Rule(platform, "{\"status\":\"LOST\"," + reason + "}"));
		MATRIX.put("PUT /api/v1/ops/vehicles/{id}/limits",
				new Rule(platform, "{\"kind\":\"VOLUME\",\"period\":\"DAY\",\"maxValue\":1000}"));
		MATRIX.put("DELETE /api/v1/ops/vehicles/{id}/limits/{limitId}", new Rule(platform, null));
		MATRIX.put("GET /api/v1/company/vehicles", new Rule(company, null));
		MATRIX.put("GET /api/v1/company/vehicles/{id}", new Rule(company, null));

		MATRIX.put("GET /api/v1/ops/operators", new Rule(platform, null));
		MATRIX.put("POST /api/v1/ops/operators", new Rule(platform, "{" + names + "}"));
		MATRIX.put("GET /api/v1/ops/operators/{id}", new Rule(platform, null));
		MATRIX.put("PUT /api/v1/ops/operators/{id}", new Rule(platform, "{" + names + "}"));
		MATRIX.put("POST /api/v1/ops/operators/{id}/status", new Rule(platform, "{\"status\":\"SUSPENDED\"," + reason + "}"));
		MATRIX.put("GET /api/v1/ops/stations", new Rule(platform, null));
		MATRIX.put("POST /api/v1/ops/stations",
				new Rule(platform, "{\"operatorId\":" + unknown + ",\"station\":" + stationBody + "}"));
		MATRIX.put("GET /api/v1/ops/stations/{id}", new Rule(platform, null));
		MATRIX.put("PUT /api/v1/ops/stations/{id}", new Rule(platform, stationBody));
		MATRIX.put("POST /api/v1/ops/stations/{id}/status", new Rule(platform, "{\"status\":\"SUSPENDED\"," + reason + "}"));
		MATRIX.put("GET /api/v1/ops/devices", new Rule(platform, null));
		MATRIX.put("POST /api/v1/ops/devices", new Rule(platform, "{\"stationId\":" + unknown + "}"));
		MATRIX.put("GET /api/v1/ops/devices/{id}", new Rule(platform, null));
		MATRIX.put("POST /api/v1/ops/devices/{id}/activation-code", new Rule(platform, null));
		MATRIX.put("POST /api/v1/ops/devices/{id}/revoke", new Rule(platform, "{" + reason + "}"));
		MATRIX.put("GET /api/v1/ops/agents", new Rule(platform, null));
		MATRIX.put("POST /api/v1/ops/agents", new Rule(platform, "{\"stationId\":" + unknown + ",\"fullName\":\"M\"}"));
		MATRIX.put("GET /api/v1/ops/agents/{id}", new Rule(platform, null));
		MATRIX.put("POST /api/v1/ops/agents/{id}/reset-pin", new Rule(platform, null));
		MATRIX.put("POST /api/v1/ops/agents/{id}/status", new Rule(platform, "{\"status\":\"SUSPENDED\"," + reason + "}"));
		MATRIX.put("GET /api/v1/station/operator", new Rule(station, null));
		MATRIX.put("GET /api/v1/station/stations", new Rule(station, null));
		MATRIX.put("GET /api/v1/station/stations/{id}", new Rule(station, null));
		MATRIX.put("GET /api/v1/station/devices", new Rule(station, null));
		MATRIX.put("GET /api/v1/station/agents", new Rule(station, null));

		MATRIX.put("GET /api/v1/ops/prices/current", new Rule(platform, null));
		MATRIX.put("GET /api/v1/ops/prices", new Rule(platform, null));
		// An unknown station: allowed callers get 400 and no price is actually scheduled.
		MATRIX.put("POST /api/v1/ops/prices",
				new Rule(platform, "{\"fuelType\":\"GAS_92\",\"stationId\":" + unknown + ",\"pricePiastres\":1500}"));
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
			String path = entry.getKey().substring(method.length() + 1).replaceAll("\\{[^}]+}", UUID.randomUUID().toString());
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
