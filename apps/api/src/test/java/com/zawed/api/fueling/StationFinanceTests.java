package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.time.LocalDate;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;

/**
 * Station finance end to end (B6 contract): a station is paid only to a verified account, never more
 * than is available, large payouts and reversals need a second person, held offline payables wait
 * for Operations, and the station reads its own position and nobody else's.
 *
 * <p>Lives beside the POS test support because every case starts with real fuelings. The bank
 * change hold is zero here so payouts can follow an approval; {@code BankAccountHoldTests} checks
 * the hold itself with the default window.
 */
@TestPropertySource(properties = { "zawed.finance.bank-change-hold-hours=0",
		"zawed.finance.payout-approval-threshold-piastres=100000", "zawed.sync.offline-exposure-cap-piastres=10000",
		"zawed.sync.company-offline-alert-piastres=40000" })
class StationFinanceTests extends OfflineTestSupport {

	private SignedIn secondOps;

	@BeforeEach
	void signInSecondOps() throws Exception {
		secondOps = signIn(seed(Role.OPS));
	}

	@Test
	void payoutsGoOnlyToAVerifiedAccountAndNeverExceedWhatIsAvailable() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 1_000_000);
		assertThat(fuel(pump, car(company), 20_000).statusCode()).isEqualTo(201); // 30,000 piastres
		String station = pump.stationId();

		HttpResponse<String> position = authed("GET", "/api/v1/ops/stations/" + station + "/payable", ops.accessToken(), null);
		assertThat(JsonPath.<Integer>read(position.body(), "$.balancePiastres")).isEqualTo(30_000);
		assertThat(JsonPath.<Integer>read(position.body(), "$.availablePiastres")).isEqualTo(30_000);

		assertThat(code(payout(ops, station, 10_000, ref()))).isEqualTo("BANK_ACCOUNT_REQUIRED");

		String change = requestBankChange(station);
		HttpResponse<String> own = authed("POST", "/api/v1/ops/bank-account-changes/" + change + "/approve",
				ops.accessToken(), null);
		assertThat(code(own)).isEqualTo("SELF_APPROVAL_NOT_ALLOWED");
		HttpResponse<String> approved = authed("POST", "/api/v1/ops/bank-account-changes/" + change + "/approve",
				secondOps.accessToken(), null);
		assertThat(approved.statusCode()).as(approved.body()).isEqualTo(200);
		assertThat((String) JsonPath.read(approved.body(), "$.status")).isEqualTo("ACTIVE");
		assertThat(approved.body()).doesNotContain("0019000500000000263180002");

		assertThat(code(payout(ops, station, 40_000, ref()))).isEqualTo("PAYOUT_EXCEEDS_AVAILABLE");
		String reference = ref();
		HttpResponse<String> paid = payout(ops, station, 20_000, reference);
		assertThat(paid.statusCode()).as(paid.body()).isEqualTo(201);
		assertThat((String) JsonPath.read(paid.body(), "$.status")).isEqualTo("POSTED");
		assertThat(stationPayable(station)).isEqualTo(10_000);
		assertThat(code(payout(ops, station, 1_000, reference))).isEqualTo("BANK_REFERENCE_IN_USE");

		// The station manager reads the same position, without Operations' pending figure.
		SignedIn manager = manager(pump.operatorId());
		HttpResponse<String> mine = authed("GET", "/api/v1/station/stations/" + station + "/payable", manager.accessToken(),
				null);
		assertThat(JsonPath.<Integer>read(mine.body(), "$.balancePiastres")).isEqualTo(10_000);
		assertThat((Object) JsonPath.read(mine.body(), "$.pendingPayoutsPiastres")).isNull();
		List<String> directions = JsonPath.read(authed("GET", "/api/v1/station/stations/" + station + "/statement",
				manager.accessToken(), null).body(), "$.items[*].direction");
		assertThat(directions).containsExactly("DEBIT", "CREDIT");
		HttpResponse<String> bank = authed("GET", "/api/v1/station/stations/" + station + "/bank-account",
				manager.accessToken(), null);
		assertThat((String) JsonPath.read(bank.body(), "$.active.accountLast4")).isEqualTo("0002");
		assertThat(bank.headers().firstValue("Cache-Control").orElse("")).contains("no-store");
		List<String> payouts = JsonPath.read(authed("GET", "/api/v1/station/payouts", manager.accessToken(), null).body(),
				"$.items[*].bankReference");
		assertThat(payouts).containsExactly(reference);

		// Another operator's manager does not learn the station exists.
		SignedIn stranger = manager(operator());
		assertThat(authed("GET", "/api/v1/station/stations/" + station + "/payable", stranger.accessToken(), null)
			.statusCode()).isEqualTo(404);
		List<String> none = JsonPath.read(authed("GET", "/api/v1/station/payouts", stranger.accessToken(), null).body(),
				"$.items[*].id");
		assertThat(none).isEmpty();
	}

	@Test
	void aLargePayoutWaitsForASecondPersonAndReservesItsAmount() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 1_000_000);
		assertThat(fuel(pump, car(company, "GAS_92", "NORMAL", null, 100_000), 80_000).statusCode()).isEqualTo(201);
		String station = pump.stationId(); // owed 120,000
		approveBankChange(requestBankChange(station));

		HttpResponse<String> waiting = payout(ops, station, 110_000, ref());
		assertThat(waiting.statusCode()).as(waiting.body()).isEqualTo(201);
		assertThat((String) JsonPath.read(waiting.body(), "$.status")).isEqualTo("PENDING");
		assertThat((Boolean) JsonPath.read(waiting.body(), "$.approvalRequired")).isTrue();
		assertThat(stationPayable(station)).isEqualTo(120_000);
		assertThat(code(payout(ops, station, 20_000, ref()))).as("only 10,000 is left once 110,000 is reserved")
			.isEqualTo("PAYOUT_EXCEEDS_AVAILABLE");

		String id = JsonPath.read(waiting.body(), "$.id");
		assertThat(code(authed("POST", "/api/v1/ops/payouts/" + id + "/approve", ops.accessToken(), null)))
			.isEqualTo("SELF_APPROVAL_NOT_ALLOWED");
		HttpResponse<String> posted = authed("POST", "/api/v1/ops/payouts/" + id + "/approve", secondOps.accessToken(), null);
		assertThat((String) JsonPath.read(posted.body(), "$.status")).as(posted.body()).isEqualTo("POSTED");
		assertThat(stationPayable(station)).isEqualTo(10_000);
	}

	@Test
	void aReversalNeedsTwoPeopleAndRefundsTheCompany() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		HttpResponse<String> fueled = fuel(pump, car(company), 20_000);
		String fueling = JsonPath.read(fueled.body(), "$.id");
		assertThat(walletBalance(company)).isEqualTo(70_000);

		HttpResponse<String> requested = authed("POST", "/api/v1/ops/fuelings/" + fueling + "/reversals", ops.accessToken(),
				"{\"reason\":\"Charged the wrong vehicle\"}");
		assertThat(requested.statusCode()).as(requested.body()).isEqualTo(201);
		String id = JsonPath.read(requested.body(), "$.id");
		assertThat(code(authed("POST", "/api/v1/ops/reversals/" + id + "/approve", ops.accessToken(), null)))
			.isEqualTo("SELF_APPROVAL_NOT_ALLOWED");
		HttpResponse<String> approved = authed("POST", "/api/v1/ops/reversals/" + id + "/approve", secondOps.accessToken(),
				null);
		assertThat((String) JsonPath.read(approved.body(), "$.status")).as(approved.body()).isEqualTo("APPROVED");

		assertThat(walletBalance(company)).isEqualTo(100_000);
		assertThat(stationPayable(pump.stationId())).isZero();
		HttpResponse<String> again = authed("POST", "/api/v1/ops/fuelings/" + fueling + "/reversals", ops.accessToken(),
				"{\"reason\":\"twice\"}");
		assertThat(again.statusCode()).isEqualTo(409);

		// The company sees the refund and why, not which staff member did it.
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, new Scope(ScopeType.COMPANY, UUID.fromString(company)), false));
		HttpResponse<String> seen = authed("GET", "/api/v1/company/reversals", fleet.accessToken(), null);
		assertThat(JsonPath.<List<String>>read(seen.body(), "$.items[*].id")).containsExactly(id);
		assertThat((Object) JsonPath.read(seen.body(), "$.items[0].requestedBy")).isNull();
	}

	@Test
	void aHeldOfflinePayableIsNotPaidUntilOperationsReleasesIt() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 1_000_000);
		String station = pump.stationId();
		approveBankChange(requestBankChange(station));

		// 30,000 offline is past this test's exposure cap of 10,000: recorded, and its payable held.
		HttpResponse<String> synced = sync(pump, capture(pump, car(company), 20_000));
		assertThat(conflicts(synced, 0)).contains("OVER_OFFLINE_ALLOWANCE");
		String fueling = fuelingId(synced, 0);

		HttpResponse<String> position = authed("GET", "/api/v1/ops/stations/" + station + "/payable", ops.accessToken(), null);
		assertThat(JsonPath.<Integer>read(position.body(), "$.balancePiastres")).isEqualTo(30_000);
		assertThat(JsonPath.<Integer>read(position.body(), "$.heldPiastres")).isEqualTo(30_000);
		assertThat(JsonPath.<Integer>read(position.body(), "$.availablePiastres")).isZero();
		assertThat(code(payout(ops, station, 1_000, ref()))).isEqualTo("PAYOUT_EXCEEDS_AVAILABLE");

		HttpResponse<String> released = authed("POST", "/api/v1/ops/fuelings/" + fueling + "/payable-hold/release",
				ops.accessToken(), "{\"reason\":\"Checked with the station\"}");
		assertThat(released.statusCode()).as(released.body()).isEqualTo(200);
		assertThat(code(authed("POST", "/api/v1/ops/fuelings/" + fueling + "/payable-hold/release", ops.accessToken(),
				"{\"reason\":\"again\"}"))).isEqualTo("HOLD_ALREADY_RELEASED");
		assertThat(payout(ops, station, 30_000, ref()).statusCode()).isEqualTo(201);
	}

	// ------------------------------------------------------------------------------------ helpers

	private String requestBankChange(String station) throws Exception {
		HttpResponse<String> requested = authed("POST", "/api/v1/ops/stations/" + station + "/bank-account/changes",
				ops.accessToken(), "{\"accountNumber\":\"EG38 0019 0005 0000 0000 2631 80002\",\"bankName\":\"Bank\","
						+ "\"holderName\":\"Station Co\",\"bankLetterReference\":\"BL-" + UUID.randomUUID() + "\"}");
		assertThat(requested.statusCode()).as(requested.body()).isEqualTo(201);
		return JsonPath.read(requested.body(), "$.id");
	}

	private void approveBankChange(String change) throws Exception {
		HttpResponse<String> approved = authed("POST", "/api/v1/ops/bank-account-changes/" + change + "/approve",
				secondOps.accessToken(), null);
		assertThat(approved.statusCode()).as(approved.body()).isEqualTo(200);
	}

	private HttpResponse<String> payout(SignedIn who, String station, long piastres, String reference) throws Exception {
		return authed("POST", "/api/v1/ops/stations/" + station + "/payouts", who.accessToken(), "{\"amountPiastres\":"
				+ piastres + ",\"bankReference\":\"" + reference + "\",\"valueDate\":\"" + LocalDate.now() + "\"}");
	}

	private SignedIn manager(String operator) throws Exception {
		return signIn(seed(Role.STATION_MANAGER, new Scope(ScopeType.OPERATOR, UUID.fromString(operator)), false));
	}

	private static String ref() {
		return "PO-" + UUID.randomUUID().toString().substring(0, 12).toUpperCase();
	}

}
