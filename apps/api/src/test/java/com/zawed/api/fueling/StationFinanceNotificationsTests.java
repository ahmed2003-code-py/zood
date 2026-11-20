package com.zawed.api.fueling;

import static com.zawed.api.support.Notified.about;
import static com.zawed.api.support.Notified.params;
import static com.zawed.api.support.Notified.typesAbout;
import static com.zawed.api.support.Notified.unread;
import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.time.LocalDate;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;

/**
 * Notifications for a station's manager (B7 W5): a bank account change decided, and a payout that
 * posted. The operator's own manager hears of them; another operator, a company and Operations do
 * not. The properties are {@code StationFinanceTests}' own, so both share one application context.
 */
@TestPropertySource(properties = { "zawed.finance.bank-change-hold-hours=0",
		"zawed.finance.payout-approval-threshold-piastres=100000", "zawed.sync.offline-exposure-cap-piastres=10000",
		"zawed.sync.company-offline-alert-piastres=40000" })
class StationFinanceNotificationsTests extends OfflineTestSupport {

	private SignedIn secondOps;

	@BeforeEach
	void signInSecondOps() throws Exception {
		secondOps = signIn(seed(Role.OPS));
	}

	@Test
	void theStationManagerHearsOfTheBankAccountAndThePayoutAndNobodyElse() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 1_000_000);
		assertThat(fuel(pump, car(company), 20_000).statusCode()).isEqualTo(201); // owed 30,000
		String station = pump.stationId();
		SignedIn manager = manager(pump.operatorId());
		SignedIn stranger = manager(operator());
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(UUID.fromString(company)), false));

		String change = requestBankChange(station);
		assertThat(unread(this, manager)).as("a request is not a decision").isZero();
		approve(change);
		assertThat(params(about(this, manager, change, "BANK_ACCOUNT_CHANGE_APPROVED"))).containsEntry("stationId", station);

		HttpResponse<String> paid = payout(station, 20_000);
		assertThat(paid.statusCode()).as(paid.body()).isEqualTo(201);
		String payout = JsonPath.read(paid.body(), "$.id");
		var posted = about(this, manager, payout, "PAYOUT_POSTED");
		assertThat(posted.get("entityType")).isEqualTo("PAYOUT");
		assertThat(params(posted)).containsEntry("stationId", station).containsEntry("amountPiastres", 20_000);
		assertThat(posted.toString()).as("no bank reference or account number").doesNotContain("PO-");

		assertThat(unread(this, stranger)).isZero();
		assertThat(typesAbout(this, fleet, payout)).isEmpty();
		assertThat(typesAbout(this, ops, payout)).isEmpty();
		assertThat(typesAbout(this, secondOps, change)).isEmpty();
	}

	@Test
	void aRejectedChangeIsToldAndAPayoutIsToldOnlyOnceItPosts() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 1_000_000);
		assertThat(fuel(pump, car(company, "GAS_92", "NORMAL", null, 100_000), 80_000).statusCode()).isEqualTo(201);
		String station = pump.stationId(); // owed 120,000
		SignedIn manager = manager(pump.operatorId());

		String refused = requestBankChange(station);
		HttpResponse<String> rejected = authed("POST", "/api/v1/ops/bank-account-changes/" + refused + "/reject",
				secondOps.accessToken(), "{\"reason\":\"The letter is not signed\"}");
		assertThat(rejected.statusCode()).as(rejected.body()).isEqualTo(200);
		var told = about(this, manager, refused, "BANK_ACCOUNT_CHANGE_REJECTED");
		assertThat(told.toString()).doesNotContain("signed");

		approve(requestBankChange(station));
		HttpResponse<String> waiting = payout(station, 110_000);
		assertThat((String) JsonPath.read(waiting.body(), "$.status")).as(waiting.body()).isEqualTo("PENDING");
		String payout = JsonPath.read(waiting.body(), "$.id");
		assertThat(typesAbout(this, manager, payout)).as("waiting for a second person: nothing paid yet").isEmpty();

		HttpResponse<String> approved = authed("POST", "/api/v1/ops/payouts/" + payout + "/approve", secondOps.accessToken(),
				null);
		assertThat((String) JsonPath.read(approved.body(), "$.status")).as(approved.body()).isEqualTo("POSTED");
		assertThat(params(about(this, manager, payout, "PAYOUT_POSTED"))).containsEntry("amountPiastres", 110_000);
	}

	// ------------------------------------------------------------------------------------ helpers

	private String requestBankChange(String station) throws Exception {
		HttpResponse<String> requested = authed("POST", "/api/v1/ops/stations/" + station + "/bank-account/changes",
				ops.accessToken(), "{\"accountNumber\":\"EG38 0019 0005 0000 0000 2631 80002\",\"bankName\":\"Bank\","
						+ "\"holderName\":\"Station Co\",\"bankLetterReference\":\"BL-" + UUID.randomUUID() + "\"}");
		assertThat(requested.statusCode()).as(requested.body()).isEqualTo(201);
		return JsonPath.read(requested.body(), "$.id");
	}

	private void approve(String change) throws Exception {
		HttpResponse<String> approved = authed("POST", "/api/v1/ops/bank-account-changes/" + change + "/approve",
				secondOps.accessToken(), null);
		assertThat(approved.statusCode()).as(approved.body()).isEqualTo(200);
	}

	private HttpResponse<String> payout(String station, long piastres) throws Exception {
		return authed("POST", "/api/v1/ops/stations/" + station + "/payouts", ops.accessToken(), "{\"amountPiastres\":"
				+ piastres + ",\"bankReference\":\"PO-" + UUID.randomUUID().toString().substring(0, 12).toUpperCase()
				+ "\",\"valueDate\":\"" + LocalDate.now() + "\"}");
	}

	private SignedIn manager(String operator) throws Exception {
		return signIn(seed(Role.STATION_MANAGER, Scope.operator(UUID.fromString(operator)), false));
	}

}
