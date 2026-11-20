package com.zawed.api.fueling;

import static com.zawed.api.support.Notified.about;
import static com.zawed.api.support.Notified.params;
import static com.zawed.api.support.Notified.typesAbout;
import static com.zawed.api.support.Notified.unread;
import static org.assertj.core.api.Assertions.assertThat;

import java.util.Map;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;

/**
 * Notifications caused by fuelings (B7 W5): a wallet falling below its threshold tells the company's
 * Fleet Manager and Accountant, and every hard alert tells Operations; nobody else hears of either.
 * They are written in the fueling's own transaction.
 */
class FuelingNotificationsTests extends PosTestSupport {

	@Test
	void aFuelingBelowTheThresholdTellsTheCompanyAndOperationsAndNobodyElse() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany(50_000).toString();
		credit(company, 60_000);
		Car car = car(company);
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(UUID.fromString(company)), false));
		SignedIn accountant = signIn(seed(Role.ACCOUNTANT, Scope.company(UUID.fromString(company)), false));
		SignedIn otherFleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(seedCompany()), false));
		SignedIn manager = signIn(seed(Role.STATION_MANAGER, Scope.operator(UUID.fromString(pump.operatorId())), false));

		assertThat(status(fuel(pump, car, 10_000))).isEqualTo("COMPLETED"); // 60,000 → 45,000: below 50,000

		for (SignedIn member : new SignedIn[] { fleet, accountant }) {
			Map<String, Object> low = about(this, member, company, "WALLET_LOW_BALANCE");
			assertThat(low.get("entityType")).isEqualTo("COMPANY_WALLET");
			assertThat(params(low)).containsEntry("balancePiastres", 45_000)
				.containsEntry("thresholdPiastres", 50_000)
				.containsKey("transactionId");
		}
		String alert = JsonPath.read(authed("GET", "/api/v1/ops/alerts?companyId=" + company + "&type=LOW_BALANCE",
				ops.accessToken(), null).body(), "$.items[0].id");
		Map<String, Object> raised = about(this, ops, alert, "HARD_ALERT_RAISED");
		assertThat(raised.get("entityType")).isEqualTo("ALERT");
		assertThat(params(raised)).containsEntry("alertType", "LOW_BALANCE")
			.containsEntry("severity", "WARNING")
			.containsEntry("companyId", company);
		assertThat(typesAbout(this, ops, company)).as("a company's wallet notice is the company's").isEmpty();
		assertThat(typesAbout(this, fleet, alert)).as("hard alerts are Operations' notices").isEmpty();
		assertThat(unread(this, otherFleet)).isZero();
		assertThat(unread(this, manager)).isZero();

		// Already below: the next fueling crosses nothing and tells nobody again.
		assertThat(status(fuel(pump, car(company), 1_000))).isEqualTo("COMPLETED");
		assertThat(typesAbout(this, fleet, company)).containsExactly("WALLET_LOW_BALANCE");
	}

	@Test
	void moreThanTheTankHoldsTellsOperationsOnly() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 1_000_000);
		Car car = car(company, "GAS_92", "NORMAL", null, 40_000);
		SignedIn opsManager = signIn(seed(Role.OPS_MANAGER));
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(UUID.fromString(company)), false));

		String fueling = JsonPath.read(fuel(pump, car, 45_000).body(), "$.id");

		String alert = JsonPath.read(authed("GET", "/api/v1/ops/alerts?companyId=" + company + "&type=TANK_CAPACITY_EXCEEDED",
				ops.accessToken(), null).body(), "$.items[0].id");
		for (SignedIn operations : new SignedIn[] { ops, opsManager }) {
			Map<String, Object> raised = about(this, operations, alert, "HARD_ALERT_RAISED");
			assertThat(params(raised)).containsEntry("alertType", "TANK_CAPACITY_EXCEEDED")
				.containsEntry("severity", "CRITICAL")
				.containsEntry("fuelingId", fueling)
				.containsEntry("stationId", pump.stationId());
		}
		assertThat(typesAbout(this, fleet, alert)).isEmpty();
	}

	private static String status(java.net.http.HttpResponse<String> response) {
		assertThat(response.statusCode()).as(response.body()).isEqualTo(201);
		return JsonPath.read(response.body(), "$.status");
	}

}
