package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.platform.security.Role;
import com.zawed.api.support.Owner;

/** Contract §4, Super Admin and Ops sections. */
class UserAdminTests extends IdentityTestSupport {

	@Test
	void aSuperAdminCreatesAUserWhoMustChangeTheTemporaryPassword() throws Exception {
		SignedIn admin = signIn(seed(Role.SUPER_ADMIN));
		String email = "new-" + UUID.randomUUID() + "@zawed.test";
		UUID company = UUID.randomUUID();

		HttpResponse<String> created = authed("POST", "/api/v1/admin/users", admin.accessToken(),
				"{\"email\":\"" + email + "\",\"fullName\":\"Khaled Fouad\",\"role\":\"FLEET_MANAGER\","
						+ "\"scope\":{\"type\":\"COMPANY\",\"id\":\"" + company + "\"}}");

		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		String temporary = JsonPath.read(created.body(), "$.temporaryPassword");
		assertThat(temporary).hasSize(16);
		assertThat(JsonPath.<Boolean>read(created.body(), "$.user.mustChangePassword")).isTrue();
		HttpResponse<String> login = post("/api/v1/auth/login", loginBody(email, temporary));
		assertThat(JsonPath.<Boolean>read(login.body(), "$.session.user.mustChangePassword")).isTrue();
		// Audited, in the user's own scope, by the admin who did it.
		assertThat(Owner.queryOne("select scope_id from audit_log where action = 'USER_CREATED' and actor_id = ?",
				admin.userId())).isEqualTo(company);
	}

	@Test
	void aRoleAndAScopeThatDoNotMatchAreRefused() throws Exception {
		SignedIn admin = signIn(seed(Role.SUPER_ADMIN));
		HttpResponse<String> response = authed("POST", "/api/v1/admin/users", admin.accessToken(),
				"{\"email\":\"x-" + UUID.randomUUID() + "@zawed.test\",\"fullName\":\"X\",\"role\":\"OPS\","
						+ "\"scope\":{\"type\":\"COMPANY\",\"id\":\"" + UUID.randomUUID() + "\"}}");
		assertThat(code(response)).isEqualTo("SCOPE_MISMATCH");
	}

	@Test
	void aDuplicateEmailIsRefusedWhateverItsCase() throws Exception {
		SignedIn admin = signIn(seed(Role.SUPER_ADMIN));
		SeededUser existing = seed(Role.ACCOUNTANT);
		HttpResponse<String> response = authed("POST", "/api/v1/admin/users", admin.accessToken(),
				"{\"email\":\"" + existing.email().toUpperCase() + "\",\"fullName\":\"X\",\"role\":\"ACCOUNTANT\","
						+ "\"scope\":{\"type\":\"COMPANY\",\"id\":\"" + UUID.randomUUID() + "\"}}");
		assertThat(response.statusCode()).isEqualTo(409);
		assertThat(code(response)).isEqualTo("EMAIL_IN_USE");
	}

	@Test
	void opsCanResetACompanyUsersPasswordButCannotSeeAdministrators() throws Exception {
		SignedIn ops = signIn(seed(Role.OPS));
		SeededUser manager = seed(Role.FLEET_MANAGER);
		SignedIn managerSession = signIn(manager);
		SeededUser admin = seed(Role.SUPER_ADMIN);

		HttpResponse<String> reset = authed("POST", "/api/v1/admin/users/" + manager.id() + "/reset-password",
				ops.accessToken(), null);
		assertThat(reset.statusCode()).isEqualTo(200);
		assertThat(authed("GET", "/api/v1/auth/me", managerSession.accessToken(), null).statusCode()).isEqualTo(401);

		// To Ops, an administrator does not exist.
		assertThat(authed("POST", "/api/v1/admin/users/" + admin.id() + "/reset-password", ops.accessToken(), null)
			.statusCode()).isEqualTo(404);
		HttpResponse<String> list = authed("GET", "/api/v1/admin/users?role=SUPER_ADMIN", ops.accessToken(), null);
		assertThat(JsonPath.<java.util.List<?>>read(list.body(), "$.items")).isEmpty();
	}

	@Test
	void opsCannotCreateUsersOrChangeStatus() throws Exception {
		SignedIn ops = signIn(seed(Role.OPS));
		SeededUser manager = seed(Role.FLEET_MANAGER);
		assertThat(authed("POST", "/api/v1/admin/users/" + manager.id() + "/status", ops.accessToken(),
				"{\"status\":\"SUSPENDED\",\"reason\":\"test\"}").statusCode()).isEqualTo(403);
	}

	@Test
	void aSuperAdminCannotSuspendThemselves() throws Exception {
		SeededUser admin = seed(Role.SUPER_ADMIN);
		SignedIn session = signIn(admin);
		HttpResponse<String> response = authed("POST", "/api/v1/admin/users/" + admin.id() + "/status",
				session.accessToken(), "{\"status\":\"SUSPENDED\",\"reason\":\"test\"}");
		assertThat(code(response)).isEqualTo("SELF_CHANGE_NOT_ALLOWED");
	}

	@Test
	void suspendingAUserIsAuditedWithTheReasonAndEndsTheirSessions() throws Exception {
		SignedIn admin = signIn(seed(Role.SUPER_ADMIN));
		SeededUser manager = seed(Role.STATION_MANAGER);
		SignedIn managerSession = signIn(manager);

		HttpResponse<String> response = authed("POST", "/api/v1/admin/users/" + manager.id() + "/status",
				admin.accessToken(), "{\"status\":\"SUSPENDED\",\"reason\":\"Left the company\"}");

		assertThat(response.statusCode()).isEqualTo(204);
		assertThat(authed("GET", "/api/v1/auth/me", managerSession.accessToken(), null).statusCode()).isEqualTo(401);
		assertThat(Owner.queryOne(
				"select metadata->>'reason' from audit_log where action = 'USER_STATUS_CHANGED' and entity_id = ?",
				manager.id())).isEqualTo("Left the company");
	}

	@Test
	void theAuditLogIsReadableBySuperAdminsOnly() throws Exception {
		SignedIn admin = signIn(seed(Role.SUPER_ADMIN));
		SignedIn ops = signIn(seed(Role.OPS));

		HttpResponse<String> page = authed("GET", "/api/v1/admin/audit?limit=5", admin.accessToken(), null);
		assertThat(page.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<java.util.List<?>>read(page.body(), "$.items")).hasSizeLessThanOrEqualTo(5).isNotEmpty();
		assertThat(authed("GET", "/api/v1/admin/audit", ops.accessToken(), null).statusCode()).isEqualTo(403);
	}

	@Test
	void aBadCursorIsABadRequestNotAnError() throws Exception {
		SignedIn admin = signIn(seed(Role.SUPER_ADMIN));
		HttpResponse<String> response = authed("GET", "/api/v1/admin/users?cursor=not-an-id", admin.accessToken(), null);
		assertThat(response.statusCode()).isEqualTo(400);
		assertThat(code(response)).isEqualTo("INVALID_CURSOR");
	}

}
