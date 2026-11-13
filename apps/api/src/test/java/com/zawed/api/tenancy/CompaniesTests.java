package com.zawed.api.tenancy;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Owner;

/** Company onboarding by Ops and the company's read-only view of itself. */
class CompaniesTests extends IdentityTestSupport {

	@Test
	void opsOnboardsACompanyWithTheDefaultCooldown() throws Exception {
		SignedIn ops = signIn(seed(Role.OPS));
		String taxId = "TAX-" + UUID.randomUUID().toString().substring(0, 8);
		HttpResponse<String> created = authed("POST", "/api/v1/ops/companies", ops.accessToken(),
				"{\"company\":{\"legalName\":\"Nile Logistics S.A.E.\",\"nameAr\":\"النيل للخدمات اللوجستية\","
						+ "\"nameEn\":\"Nile Logistics\",\"taxId\":\"" + taxId + "\",\"contactEmail\":\"Ops@Nile.test\"},"
						+ "\"lowBalanceThresholdPiastres\":500000}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		assertThat(JsonPath.<Integer>read(created.body(), "$.cooldownMinutes")).isEqualTo(180);
		assertThat(JsonPath.<String>read(created.body(), "$.contactEmail")).isEqualTo("ops@nile.test");

		HttpResponse<String> duplicate = authed("POST", "/api/v1/ops/companies", ops.accessToken(),
				"{\"company\":{\"legalName\":\"Other\",\"nameAr\":\"أخرى\",\"nameEn\":\"Other\",\"taxId\":\"" + taxId
						+ "\"},\"lowBalanceThresholdPiastres\":0}");
		assertThat(code(duplicate)).isEqualTo("TAX_ID_IN_USE");
	}

	@Test
	void onlyASuperAdminChangesTheCooldownAndTheChangeIsAudited() throws Exception {
		UUID company = seedCompany();
		String body = "{\"cooldownMinutes\":240,\"lowBalanceThresholdPiastres\":100000,\"reason\":\"Contract\"}";
		assertThat(authed("PUT", "/api/v1/ops/companies/" + company + "/policy", signIn(seed(Role.OPS)).accessToken(),
				body).statusCode()).isEqualTo(403);

		HttpResponse<String> changed = authed("PUT", "/api/v1/ops/companies/" + company + "/policy",
				signIn(seed(Role.SUPER_ADMIN)).accessToken(), body);
		assertThat(changed.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<Integer>read(changed.body(), "$.cooldownMinutes")).isEqualTo(240);
		String metadata = Owner.queryOne(
				"select metadata::text from audit_log where action = 'COMPANY_POLICY_CHANGED' and entity_id = ?", company);
		assertThat(metadata).contains("\"from\": 180").contains("\"to\": 240").contains("Contract");

		HttpResponse<String> outOfRange = authed("PUT", "/api/v1/ops/companies/" + company + "/policy",
				signIn(seed(Role.SUPER_ADMIN)).accessToken(),
				"{\"cooldownMinutes\":60,\"lowBalanceThresholdPiastres\":0,\"reason\":\"x\"}");
		assertThat(outOfRange.statusCode()).isEqualTo(400);
	}

	@Test
	void aCompanyReadsOnlyItself() throws Exception {
		UUID mine = seedCompany();
		UUID theirs = seedCompany();
		SignedIn ops = signIn(seed(Role.OPS));
		authed("POST", "/api/v1/ops/companies/" + mine + "/departments", ops.accessToken(), "{\"name\":\"Sales\"}");
		authed("POST", "/api/v1/ops/companies/" + theirs + "/departments", ops.accessToken(), "{\"name\":\"Secret\"}");

		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, Scope.company(mine), false));
		HttpResponse<String> profile = authed("GET", "/api/v1/company/profile", manager.accessToken(), null);
		assertThat(JsonPath.<String>read(profile.body(), "$.id")).isEqualTo(mine.toString());
		assertThat(profile.body()).doesNotContain("taxId");
		List<String> departments = JsonPath.read(
				authed("GET", "/api/v1/company/departments", manager.accessToken(), null).body(), "$.items[*].name");
		assertThat(departments).containsExactly("Sales");
		// The company portal has no write endpoints, and the Ops ones are closed to it.
		assertThat(authed("GET", "/api/v1/ops/companies/" + mine, manager.accessToken(), null).statusCode())
			.isEqualTo(403);
	}

	@Test
	void aDepartmentNameIsUniqueWithinACompany() throws Exception {
		UUID company = seedCompany();
		SignedIn ops = signIn(seed(Role.OPS));
		assertThat(authed("POST", "/api/v1/ops/companies/" + company + "/departments", ops.accessToken(),
				"{\"name\":\"Field Team\"}").statusCode()).isEqualTo(201);
		HttpResponse<String> again = authed("POST", "/api/v1/ops/companies/" + company + "/departments",
				ops.accessToken(), "{\"name\":\"field  team\"}");
		assertThat(again.statusCode()).isEqualTo(409);
		assertThat(code(again)).isEqualTo("DEPARTMENT_EXISTS");
	}

}
