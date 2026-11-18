package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.time.Instant;
import java.time.LocalDate;
import java.time.temporal.ChronoUnit;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.platform.security.Role;

/**
 * A newly approved bank account receives nothing during the hold window (default 72 hours, roadmap
 * delta A3): a fraudulent change has time to be noticed before money follows it.
 */
class BankAccountHoldTests extends PosTestSupport {

	@Test
	void noPayoutGoesToANewAccountDuringTheHold() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		assertThat(fuel(pump, car(company), 20_000).statusCode()).isEqualTo(201);
		String station = pump.stationId();

		HttpResponse<String> requested = authed("POST", "/api/v1/ops/stations/" + station + "/bank-account/changes",
				ops.accessToken(), "{\"accountNumber\":\"EG380019000500000000263180002\",\"bankName\":\"Bank\","
						+ "\"holderName\":\"Station Co\",\"bankLetterReference\":\"BL-" + UUID.randomUUID() + "\"}");
		String change = JsonPath.read(requested.body(), "$.id");
		HttpResponse<String> approved = authed("POST", "/api/v1/ops/bank-account-changes/" + change + "/approve",
				signIn(seed(Role.OPS)).accessToken(), null);
		Instant heldUntil = Instant.parse(JsonPath.read(approved.body(), "$.payoutsHeldUntil"));
		assertThat(heldUntil).isAfter(Instant.now().plus(71, ChronoUnit.HOURS));

		HttpResponse<String> payout = authed("POST", "/api/v1/ops/stations/" + station + "/payouts", ops.accessToken(),
				"{\"amountPiastres\":1000,\"bankReference\":\"PO-" + UUID.randomUUID() + "\",\"valueDate\":\""
						+ LocalDate.now() + "\"}");
		assertThat(code(payout)).isEqualTo("PAYOUT_ON_HOLD");
	}

}
