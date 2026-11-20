package com.zawed.api.ledger;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Owner;

/**
 * Who asked, verified and decided, by name (QA round 1: the cards showed "Verified by 01a1f8dd").
 * Operations reads every name and role. A company reads its own users' names; a platform actor is
 * only {@code ZAWED_OPERATIONS}, never a staff member's name or e-mail. The receiving account is
 * shown by its label when one is configured.
 */
@TestPropertySource(properties = { "zawed.finance.receiving-accounts=MAIN,CIB",
		"zawed.finance.receiving-account-labels=MAIN=Banque Misr ••4821" })
class MakerCheckerNamesTests extends IdentityTestSupport {

	private static final String TODAY = LocalDate.now(ZoneId.of("Africa/Cairo")).toString();

	private SeededUser opsUser;

	private SignedIn ops;

	private SignedIn manager;

	@BeforeEach
	void signInStaff() throws Exception {
		opsUser = seed(Role.OPS);
		rename(opsUser.id(), "Hana Operations");
		ops = signIn(opsUser);
		SeededUser managerUser = seed(Role.OPS_MANAGER);
		rename(managerUser.id(), "Karim Manager");
		manager = signIn(managerUser);
	}

	@Test
	void aTopUpCarriesNamesForOperationsAndOnlyTheCompanysOwnNamesForTheCompany() throws Exception {
		UUID company = seedCompany();
		SeededUser fleetUser = seed(Role.FLEET_MANAGER, Scope.company(company), false);
		rename(fleetUser.id(), "Mona Fleet");
		SignedIn fleet = signIn(fleetUser);
		SeededUser accountantUser = seed(Role.ACCOUNTANT, Scope.company(company), false);
		SignedIn accountant = signIn(accountantUser);

		HttpResponse<String> requested = authed("POST", "/api/v1/company/topups", fleet.accessToken(),
				"{\"amountPiastres\":50000,\"bankReference\":\"FT-" + UUID.randomUUID() + "\",\"transferDate\":\"" + TODAY
						+ "\"}");
		assertThat(requested.statusCode()).as(requested.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(requested.body(), "$.requestedByName")).isEqualTo("Mona Fleet");
		assertThat(JsonPath.<String>read(requested.body(), "$.requestedByRole")).isEqualTo("FLEET_MANAGER");
		String id = JsonPath.read(requested.body(), "$.id");

		HttpResponse<String> verified = authed("POST", "/api/v1/ops/topups/" + id + "/verify", ops.accessToken(),
				"{\"receivedAmountPiastres\":50000,\"receivingAccount\":\"MAIN\"}");
		assertThat(verified.statusCode()).as(verified.body()).isEqualTo(200);
		HttpResponse<String> approved = authed("POST", "/api/v1/ops/topups/" + id + "/approve", manager.accessToken(), null);
		assertThat(approved.statusCode()).as(approved.body()).isEqualTo(200);

		// Operations: every name and role, and the account's label.
		HttpResponse<String> forOps = authed("GET", "/api/v1/ops/topups/" + id, ops.accessToken(), null);
		assertThat(JsonPath.<String>read(forOps.body(), "$.requestedByName")).isEqualTo("Mona Fleet");
		assertThat(JsonPath.<String>read(forOps.body(), "$.requestedByRole")).isEqualTo("FLEET_MANAGER");
		assertThat(JsonPath.<String>read(forOps.body(), "$.verifiedByName")).isEqualTo("Hana Operations");
		assertThat(JsonPath.<String>read(forOps.body(), "$.verifiedByRole")).isEqualTo("OPS");
		assertThat(JsonPath.<String>read(forOps.body(), "$.decidedByName")).isEqualTo("Karim Manager");
		assertThat(JsonPath.<String>read(forOps.body(), "$.decidedByRole")).isEqualTo("OPS_MANAGER");
		assertThat(JsonPath.<String>read(forOps.body(), "$.receivingAccount")).isEqualTo("MAIN");
		assertThat(JsonPath.<String>read(forOps.body(), "$.receivingAccountLabel")).isEqualTo("Banque Misr ••4821");
		List<String> listed = JsonPath.read(authed("GET", "/api/v1/ops/topups?companyId=" + company, ops.accessToken(), null)
			.body(), "$.items[*].verifiedByName");
		assertThat(listed).containsExactly("Hana Operations");

		// The company, through another of its users: its own colleague by name, ZAWED as a team.
		HttpResponse<String> forCompany = authed("GET", "/api/v1/company/topups", accountant.accessToken(), null);
		assertThat(forCompany.statusCode()).as(forCompany.body()).isEqualTo(200);
		assertThat(JsonPath.<List<String>>read(forCompany.body(), "$.items[*].requestedByName")).containsExactly("Mona Fleet");
		assertThat(JsonPath.<List<String>>read(forCompany.body(), "$.items[*].verifiedByRole"))
			.containsExactly("ZAWED_OPERATIONS");
		assertThat(JsonPath.<List<String>>read(forCompany.body(), "$.items[*].decidedByRole"))
			.containsExactly("ZAWED_OPERATIONS");
		assertThat(JsonPath.<List<Object>>read(forCompany.body(), "$.items[*].verifiedByName")).containsOnlyNulls();
		assertThat(JsonPath.<List<Object>>read(forCompany.body(), "$.items[*].decidedByName")).containsOnlyNulls();
		assertThat(forCompany.body()).as("no staff name or e-mail reaches a tenant")
			.doesNotContain("Hana", "Karim", opsUser.email(), "\"OPS\"", "OPS_MANAGER");
	}

	@Test
	void aCompanyThatCancelsSeesItsOwnUserAsTheDecider() throws Exception {
		UUID company = seedCompany();
		SeededUser accountantUser = seed(Role.ACCOUNTANT, Scope.company(company), false);
		rename(accountantUser.id(), "Sara Accounts");
		SignedIn accountant = signIn(accountantUser);
		String id = JsonPath.read(authed("POST", "/api/v1/company/topups", accountant.accessToken(),
				"{\"amountPiastres\":1000,\"bankReference\":\"FT-" + UUID.randomUUID() + "\",\"transferDate\":\"" + TODAY
						+ "\"}").body(), "$.id");

		HttpResponse<String> cancelled = authed("POST", "/api/v1/company/topups/" + id + "/cancel", accountant.accessToken(),
				null);
		assertThat(cancelled.statusCode()).as(cancelled.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(cancelled.body(), "$.decidedByName")).isEqualTo("Sara Accounts");
		assertThat(JsonPath.<String>read(cancelled.body(), "$.decidedByRole")).isEqualTo("ACCOUNTANT");
		assertThat(JsonPath.<Object>read(cancelled.body(), "$.verifiedByRole")).as("nobody verified it").isNull();
		assertThat(JsonPath.<Object>read(cancelled.body(), "$.receivingAccountLabel")).isEqualTo("Banque Misr ••4821");
	}

	@Test
	void aTransferRecordedByOperationsHasNoRequesterAndAnUnlabelledAccountKeepsItsCode() throws Exception {
		UUID company = seedCompany();
		HttpResponse<String> recorded = authed("POST", "/api/v1/ops/companies/" + company + "/wallet/credits",
				ops.accessToken(), "{\"amountPiastres\":7000,\"receivingAccount\":\"CIB\",\"bankReference\":\"FT-"
						+ UUID.randomUUID() + "\",\"transferDate\":\"" + TODAY + "\"}");
		assertThat(recorded.statusCode()).as(recorded.body()).isEqualTo(201);
		assertThat(JsonPath.<Object>read(recorded.body(), "$.requestedByName")).isNull();
		assertThat(JsonPath.<Object>read(recorded.body(), "$.requestedByRole")).isNull();
		assertThat(JsonPath.<String>read(recorded.body(), "$.verifiedByName")).isEqualTo("Hana Operations");
		assertThat(JsonPath.<String>read(recorded.body(), "$.receivingAccount")).isEqualTo("CIB");
		assertThat(JsonPath.<Object>read(recorded.body(), "$.receivingAccountLabel")).isNull();
	}

	@Test
	void aWalletAdjustmentNamesWhoAskedAndWhoDecided() throws Exception {
		UUID company = seedCompany();
		HttpResponse<String> asked = send(request("/api/v1/ops/companies/" + company + "/wallet/adjustments")
			.header("Authorization", "Bearer " + ops.accessToken())
			.header("Content-Type", "application/json")
			.header("Idempotency-Key", "names-" + UUID.randomUUID())
			.POST(java.net.http.HttpRequest.BodyPublishers.ofString("{\"amountPiastres\":3000,\"reason\":\"Goodwill\"}")));
		assertThat(asked.statusCode()).as(asked.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(asked.body(), "$.requestedByName")).isEqualTo("Hana Operations");
		assertThat(JsonPath.<String>read(asked.body(), "$.requestedByRole")).isEqualTo("OPS");
		assertThat(JsonPath.<Object>read(asked.body(), "$.decidedByName")).isNull();
		String id = JsonPath.read(asked.body(), "$.id");

		HttpResponse<String> rejected = authed("POST", "/api/v1/ops/wallet-adjustments/" + id + "/reject",
				manager.accessToken(), "{\"reason\":\"No ticket\"}");
		assertThat(rejected.statusCode()).as(rejected.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(rejected.body(), "$.decidedByName")).isEqualTo("Karim Manager");
		assertThat(JsonPath.<String>read(rejected.body(), "$.decidedByRole")).isEqualTo("OPS_MANAGER");
		List<String> listed = JsonPath.read(authed("GET", "/api/v1/ops/wallet-adjustments?companyId=" + company,
				ops.accessToken(), null).body(), "$.items[*].requestedByName");
		assertThat(listed).containsExactly("Hana Operations");
	}

	static void rename(UUID user, String name) {
		Owner.update("update users set full_name = ? where id = ?", name, user);
	}

}
