package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.time.Instant;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Owner;

/**
 * Money is integer piastres and volume integer millilitres (QA round 1). A fractional JSON number
 * in such a field is refused with 400 VALIDATION_FAILED naming the field; it is never truncated to
 * the integer below, on any endpoint, the POS included.
 */
class WholeNumberTests extends OfflineTestSupport {

	private static final String TODAY = LocalDate.now(ZoneId.of("Africa/Cairo")).toString();

	@Test
	void aCompanyTopUpOfAFractionalAmountIsRefusedNotTruncated() throws Exception {
		UUID company = IdentityTestSupport.seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String reference = "FT-" + UUID.randomUUID();

		HttpResponse<String> refused = authed("POST", "/api/v1/company/topups", fleet.accessToken(),
				"{\"amountPiastres\":100.5,\"bankReference\":\"" + reference + "\",\"transferDate\":\"" + TODAY + "\"}");

		assertRefused(refused, "amountPiastres");
		assertThat(Owner.<Long>queryOne("select count(*) from topup_requests where company_id = ?", company)).isZero();

		// A whole number still works, and so does the same value written as a JSON integer.
		HttpResponse<String> accepted = authed("POST", "/api/v1/company/topups", fleet.accessToken(),
				"{\"amountPiastres\":100,\"bankReference\":\"" + reference + "\",\"transferDate\":\"" + TODAY + "\"}");
		assertThat(accepted.statusCode()).as(accepted.body()).isEqualTo(201);
		assertThat(JsonPath.<Integer>read(accepted.body(), "$.amountPiastres")).isEqualTo(100);
	}

	@Test
	void aFloatThatHappensToBeWholeIsRefusedToo() throws Exception {
		UUID company = IdentityTestSupport.seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		for (String amount : new String[] { "100.0", "1e2", "100.000001" }) {
			HttpResponse<String> refused = authed("POST", "/api/v1/company/topups", fleet.accessToken(),
					"{\"amountPiastres\":" + amount + ",\"bankReference\":\"FT-" + UUID.randomUUID()
							+ "\",\"transferDate\":\"" + TODAY + "\"}");
			assertRefused(refused, "amountPiastres");
		}
	}

	@Test
	void opsMoneyFieldsRefuseFractions() throws Exception {
		UUID company = IdentityTestSupport.seedCompany();

		assertRefused(authed("POST", "/api/v1/ops/companies/" + company + "/wallet/adjustments", ops.accessToken(),
				"{\"amountPiastres\":2500.25,\"reason\":\"goodwill\"}"), "amountPiastres");
		assertRefused(authed("POST", "/api/v1/ops/companies/" + company + "/wallet/credits", ops.accessToken(),
				"{\"amountPiastres\":2500.75,\"bankReference\":\"FT-" + UUID.randomUUID() + "\",\"transferDate\":\""
						+ TODAY + "\"}"),
				"amountPiastres");
		assertThat(balance(company)).isZero();
		assertThat(Owner.<Long>queryOne("select count(*) from wallet_adjustments where company_id = ?", company)).isZero();

		// A nested field is named by its path.
		assertRefused(authed("POST", "/api/v1/ops/companies", ops.accessToken(),
				"{\"company\":{\"legalName\":\"F\",\"nameAr\":\"ف\",\"nameEn\":\"F\"},\"lowBalanceThresholdPiastres\":10.5}"),
				"lowBalanceThresholdPiastres");
	}

	@Test
	void limitsAndVolumesRefuseFractions() throws Exception {
		Car car = car(IdentityTestSupport.seedCompany().toString());

		assertRefused(authed("PUT", "/api/v1/ops/vehicles/" + car.vehicleId() + "/limits", ops.accessToken(),
				"{\"kind\":\"AMOUNT\",\"period\":\"DAY\",\"maxValue\":500050.5}"), "maxValue");
		assertRefused(authed("PUT", "/api/v1/ops/vehicles/" + car.vehicleId() + "/limits", ops.accessToken(),
				"{\"kind\":\"VOLUME\",\"period\":\"DAY\",\"maxValue\":40000.5}"), "maxValue");
		assertThat(Owner.<Long>queryOne("select count(*) from limit_rules where vehicle_id = ?",
				UUID.fromString(car.vehicleId()))).isZero();

		assertRefused(authed("POST", "/api/v1/ops/vehicles", ops.accessToken(),
				"{\"companyId\":\"" + car.companyId() + "\",\"vehicle\":{\"plate\":\"WN " + UUID.randomUUID().toString()
						.substring(0, 6) + "\",\"make\":\"M\",\"model\":\"M\",\"fuelType\":\"GAS_92\","
						+ "\"tankCapacityMl\":50000.5,\"tier\":\"NORMAL\"}}"),
				"vehicle.tankCapacityMl");
	}

	@Test
	void thePosRefusesAFractionalVolume() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = IdentityTestSupport.seedCompany().toString();
		credit(company, 1_000_000);
		Car car = car(company);
		UUID clientTxnId = UUID.randomUUID();

		HttpResponse<String> refused = pos("POST", "/api/v1/pos/fuelings", pump.deviceToken(), pump.session(),
				"{\"clientTxnId\":\"" + clientTxnId + "\",\"tagUid\":\"" + car.tagUid()
						+ "\",\"volumeMl\":20000.5,\"deviceAt\":\"" + Instant.now() + "\",\"receiptLocale\":\"ar\"}");

		assertRefused(refused, "volumeMl");
		assertThat(Owner.<Long>queryOne("select count(*) from fuelings where client_txn_id = ?", clientTxnId)).isZero();
		assertThat(balance(UUID.fromString(company))).isEqualTo(1_000_000);

		// A whole number written as a float is still refused online: the terminal is told at once.
		assertRefused(pos("POST", "/api/v1/pos/fuelings", pump.deviceToken(), pump.session(),
				"{\"clientTxnId\":\"" + UUID.randomUUID() + "\",\"tagUid\":\"" + car.tagUid()
						+ "\",\"volumeMl\":20000.0,\"deviceAt\":\"" + Instant.now() + "\",\"receiptLocale\":\"ar\"}"),
				"volumeMl");
	}

	/**
	 * Offline, one bad item never holds back the rest (B5 contract §3, invariant 7). Refusing the whole
	 * batch for one fractional volume would leave the device resending the same 100 items forever. A
	 * whole number written as a float ({@code 20000.0}) is that number; a real fraction is that item's
	 * REJECTED INVALID_VOLUME, with its SYNC_REJECTED alert, and every other item is recorded.
	 */
	@Test
	void aFractionalVolumeInAnOfflineBatchRejectsThatItemOnly() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = IdentityTestSupport.seedCompany().toString();
		credit(company, 10_000_000);
		Car car = car(company);
		UUID batchId = UUID.randomUUID();
		java.util.List<String> items = new java.util.ArrayList<>();
		for (int i = 0; i < 100; i++) {
			String json = capture(pump, car, 1_000).json();
			if (i == 10) {
				json = json.replace("\"volumeMl\":1000", "\"volumeMl\":1500.25");
			}
			if (i == 50) {
				json = json.replace("\"volumeMl\":1000", "\"volumeMl\":20000.0");
			}
			items.add(json);
		}
		HttpResponse<String> answered = pos("POST", "/api/v1/pos/sync", pump.deviceToken(), null,
				"{\"batchId\":\"" + batchId + "\",\"pending\":0,\"items\":[" + String.join(",", items) + "]}");

		java.util.List<String> results = results(answered);
		assertThat(results).hasSize(100);
		assertThat(rejection(answered, 10)).isEqualTo("INVALID_VOLUME");
		assertThat(results.stream().filter("REJECTED"::equals).count()).isEqualTo(1);
		assertThat(results.stream().filter("RECORDED"::equals).count()).isEqualTo(99);
		assertThat(JsonPath.<Integer>read(answered.body(), "$.results[50].fueling.volumeMl")).isEqualTo(20_000);
		assertThat(Owner.<Long>queryOne("select count(*) from fuelings where company_id = ?::uuid", company)).isEqualTo(99);
		assertThat(Owner.<Long>queryOne("select count(*) from sync_batches where device_batch_id = ?", batchId)).isEqualTo(1);
		assertThat(Owner.<String>queryOne(
				"select details::text from alerts where type = 'SYNC_REJECTED' and device_id = ?::uuid", pump.deviceId()))
			.contains("INVALID_VOLUME")
			.contains("1500.25");
	}

	private static long balance(UUID company) {
		return Owner.<Long>queryOne("select balance_piastres from company_wallets where company_id = ?", company);
	}

	private static void assertRefused(HttpResponse<String> response, String field) {
		assertThat(response.statusCode()).as(response.body()).isEqualTo(400);
		assertThat(JsonPath.<String>read(response.body(), "$.code")).isEqualTo("VALIDATION_FAILED");
		assertThat(JsonPath.<String>read(response.body(), "$.fields[0].field")).isEqualTo(field);
		assertThat(JsonPath.<String>read(response.body(), "$.fields[0].code")).isEqualTo("INTEGER_REQUIRED");
		assertThat(response.body()).as("the value is not echoed").doesNotContain("100.5", "20000.5");
	}

}
