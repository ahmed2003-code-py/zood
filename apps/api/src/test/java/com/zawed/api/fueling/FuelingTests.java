package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * The online fueling engine (plan §3.4, B4 contract §3) through the real POS API: the money, every
 * decline reason in PRD order, idempotency under a race, the hard alerts, and who may read what.
 */
class FuelingTests extends PosTestSupport {

	// ------------------------------------------------------------------------------- the money

	@Test
	void aCompletedFuelingMovesWalletToStationPayableOnceAndStartsTheCooldown() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		Car car = car(company);
		UUID txn = UUID.randomUUID();

		HttpResponse<String> done = fuel(pump, car, txn, 20_000, null); // 20 L × 15.00 = 300.00 EGP
		assertThat(done.statusCode()).as(done.body()).isEqualTo(201);
		assertThat(done.headers().firstValue("Cache-Control").orElse("")).contains("no-store");
		assertThat(JsonPath.<String>read(done.body(), "$.status")).isEqualTo("COMPLETED");
		assertThat(JsonPath.<Integer>read(done.body(), "$.amountPiastres")).isEqualTo(30_000);
		assertThat(JsonPath.<String>read(done.body(), "$.walletStatus")).isEqualTo("AVAILABLE");
		assertThat(JsonPath.<String>read(done.body(), "$.reference")).matches("F-\\d+");
		assertThat(done.body()).as("the agent never sees the balance").doesNotContain("70000").doesNotContain("balance");
		assertThat(JsonPath.<String>read(done.body(), "$.cooldownUntil")).isNotNull();

		assertThat(walletBalance(company)).isEqualTo(70_000);
		assertThat(Owner.<Long>queryOne("""
				select e.amount_piastres from ledger_entries e join ledger_accounts a on a.id = e.account_id
				where a.code = ? and e.direction = 'CREDIT'
				""", "station:" + pump.stationId() + ":payable")).isEqualTo(30_000L);
		assertThat(Owner.<Object>queryOne("select cooldown_until from rfid_tags where vehicle_id = ?::uuid", car.vehicleId()))
			.isNotNull();

		// A retry of the same clientTxnId: the stored outcome, nothing posted again.
		HttpResponse<String> again = fuel(pump, car, txn, 20_000, null);
		assertThat(again.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<Boolean>read(again.body(), "$.replayed")).isTrue();
		assertThat(JsonPath.<String>read(again.body(), "$.id")).isEqualTo(JsonPath.read(done.body(), "$.id"));
		assertThat(walletBalance(company)).isEqualTo(70_000);
		assertThat(Owner.<Long>queryOne("select count(*) from fuelings where client_txn_id = ?", txn)).isEqualTo(1L);

		// The same id for a different fueling is a client error.
		assertThat(code(fuel(pump, car, txn, 5_000, null))).isEqualTo("IDEMPOTENCY_MISMATCH");
	}

	@Test
	void fiftyParallelRetriesOfOneFuelingRecordAndChargeItOnce() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		Car car = car(company);
		UUID txn = UUID.randomUUID();

		List<Integer> statuses = Concurrently.run(50, (i) -> fuel(pump, car, txn, 10_000, null).statusCode());
		assertThat(statuses).containsOnly(200, 201);
		assertThat(statuses.stream().filter((s) -> s == 201).count()).isEqualTo(1);
		assertThat(Owner.<Long>queryOne("select count(*) from fuelings where client_txn_id = ?", txn)).isEqualTo(1L);
		assertThat(walletBalance(company)).isEqualTo(100_000 - 15_000);
		assertThat(Owner.<Long>queryOne("select count(*) from ledger_transactions where source_type = 'FUELING' "
				+ "and source_id = (select id from fuelings where client_txn_id = ?)", txn)).isEqualTo(1L);
	}

	@Test
	void parallelFuelingsOfOneCompanyNeverOverdrawTheWallet() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 15_000 * 25); // exactly twenty-five 10 L fuelings
		List<Car> cars = new ArrayList<>();
		for (int i = 0; i < 50; i++) {
			cars.add(car(company));
		}
		List<String> outcomes = Concurrently.run(50, (i) -> {
			HttpResponse<String> response = fuel(pump, cars.get(i), 10_000);
			assertThat(response.statusCode()).as(response.body()).isEqualTo(201);
			String status = JsonPath.read(response.body(), "$.status");
			return "COMPLETED".equals(status) ? status : JsonPath.read(response.body(), "$.declineReason");
		});
		assertThat(outcomes.stream().filter("COMPLETED"::equals).count()).isEqualTo(25);
		assertThat(outcomes.stream().filter("INSUFFICIENT_BALANCE"::equals).count()).isEqualTo(25);
		assertThat(walletBalance(company)).isZero();
		assertThat(Owner.<Long>queryOne("""
				select coalesce(sum(case when e.direction = 'CREDIT' then e.amount_piastres else -e.amount_piastres end), 0)::bigint
				from ledger_entries e join ledger_accounts a on a.id = e.account_id where a.code = ?
				""", "company:" + company + ":wallet")).isZero();
	}

	// --------------------------------------------------------------------------- decline reasons

	@Test
	void eachDeclineReasonIsRecordedWithoutMovingMoney() throws Exception {
		Pump pump = pump("GAS_92");
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 1_000_000);

		Car cooled = car(company);
		assertThat(status(fuel(pump, cooled, 1_000))).isEqualTo("COMPLETED");
		assertThat(reason(fuel(pump, cooled, 1_000))).isEqualTo("COOLDOWN");

		Car lost = car(company);
		String tag = Owner.<UUID>queryOne("select id from rfid_tags where vehicle_id = ?::uuid", lost.vehicleId()).toString();
		assertThat(authed("POST", "/api/v1/ops/tags/" + tag + "/status", ops.accessToken(),
				"{\"status\":\"LOST\",\"reason\":\"Driver lost it\"}").statusCode()).isEqualTo(200);
		assertThat(reason(fuel(pump, lost, 1_000))).isEqualTo("TAG_INACTIVE");

		Car suspended = car(company);
		assertThat(authed("POST", "/api/v1/ops/vehicles/" + suspended.vehicleId() + "/status", ops.accessToken(),
				"{\"status\":\"SUSPENDED\",\"reason\":\"Workshop\"}").statusCode()).isEqualTo(200);
		assertThat(reason(fuel(pump, suspended, 1_000))).isEqualTo("VEHICLE_INACTIVE");

		Car wrongFuel = car(company);
		assertThat(reason(fuel(pump, wrongFuel, UUID.randomUUID(), 1_000, "DIESEL"))).isEqualTo("FUEL_TYPE_MISMATCH");

		Car diesel = car(company, "DIESEL", "NORMAL", null, 80_000);
		assertThat(reason(fuel(pump, diesel, 1_000))).as("the station sells no diesel").isEqualTo("FUEL_NOT_AVAILABLE");

		Car limited = car(company);
		assertThat(authed("PUT", "/api/v1/ops/vehicles/" + limited.vehicleId() + "/limits", ops.accessToken(),
				"{\"kind\":\"VOLUME\",\"period\":\"DAY\",\"maxValue\":5000}").statusCode()).isEqualTo(200);
		assertThat(reason(fuel(pump, limited, 6_000))).isEqualTo("LIMIT_EXCEEDED");
		assertThat(status(fuel(pump, limited, 5_000))).as("exactly the limit").isEqualTo("COMPLETED");

		String poor = seedCompany().toString();
		Car broke = car(poor);
		HttpResponse<String> insufficient = fuel(pump, broke, 1_000);
		assertThat(reason(insufficient)).isEqualTo("INSUFFICIENT_BALANCE");
		assertThat(JsonPath.<String>read(insufficient.body(), "$.walletStatus")).isEqualTo("INSUFFICIENT");

		String frozen = seedCompany().toString();
		credit(frozen, 100_000);
		Car iced = car(frozen);
		assertThat(authed("POST", "/api/v1/ops/companies/" + frozen + "/status", ops.accessToken(),
				"{\"status\":\"FROZEN\",\"reason\":\"Unpaid\"}").statusCode()).isEqualTo(200);
		HttpResponse<String> refused = fuel(pump, iced, 1_000);
		assertThat(reason(refused)).isEqualTo("COMPANY_FROZEN");
		assertThat(JsonPath.<String>read(refused.body(), "$.walletStatus")).isEqualTo("SUSPENDED");
		assertThat(walletBalance(frozen)).isEqualTo(100_000);

		// Every declined row is recorded and none posted.
		assertThat(Owner.<Long>queryOne("select count(*) from fuelings where status = 'DECLINED' and company_id in (?::uuid, ?::uuid, ?::uuid) "
				+ "and ledger_transaction_id is not null", company, poor, frozen)).isZero();
		assertThat(Owner.<Long>queryOne("select count(*) from fuelings where status = 'DECLINED' and company_id in (?::uuid, ?::uuid, ?::uuid)",
				company, poor, frozen)).isEqualTo(8L);
	}

	@Test
	void anUnknownTagIsRefusedAndNothingIsRecorded() throws Exception {
		Pump pump = pump();
		UUID txn = UUID.randomUUID();
		HttpResponse<String> refused = pos("POST", "/api/v1/pos/fuelings", pump.deviceToken(), pump.session(),
				fuelingBody(txn, randomUid(), 1_000, null));
		assertThat(refused.statusCode()).isEqualTo(404);
		assertThat(code(refused)).isEqualTo("TAG_NOT_RECOGNISED");
		assertThat(Owner.<Long>queryOne("select count(*) from fuelings where client_txn_id = ?", txn)).isZero();
	}

	@Test
	void theVolumeMustBeSane() throws Exception {
		Pump pump = pump();
		Car car = car(seedCompany().toString());
		assertThat(code(fuel(pump, car, FuelingEngine.MAX_VOLUME_ML + 1))).isEqualTo("INVALID_VOLUME");
		assertThat(fuel(pump, car, 0).statusCode()).isEqualTo(400);
	}

	// ------------------------------------------------------------------------- VIP and alerts

	@Test
	void whileTheVipFlowIsSwitchedOffAVipVehicleFuelsLikeANormalOne() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		Car vip = car(company, "GAS_92", "VIP", 30_000L, 60_000);

		assertThat(reason(fuel(pump, vip, 10_000))).as("no money, no fuel: the VIP flow is off").isEqualTo("INSUFFICIENT_BALANCE");
		credit(company, 100_000);
		HttpResponse<String> billed = fuel(pump, vip, 40_000);
		assertThat(status(billed)).isEqualTo("COMPLETED");
		assertThat(JsonPath.<Boolean>read(billed.body(), "$.billable")).isTrue();
		assertThat(walletBalance(company)).isEqualTo(100_000 - 60_000);
		assertThat(JsonPath.<List<Object>>read(authed("GET", "/api/v1/ops/alerts?companyId=" + company + "&type=VIP_CEILING_PASSED",
				ops.accessToken(), null).body(), "$.items")).isEmpty();
	}

	@Test
	void moreThanTheTankHoldsIsApprovedButFlagged() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 1_000_000);
		Car car = car(company, "GAS_92", "NORMAL", null, 40_000);

		assertThat(status(fuel(pump, car, 45_000))).isEqualTo("COMPLETED");
		HttpResponse<String> alerts = authed("GET", "/api/v1/ops/alerts?companyId=" + company, ops.accessToken(), null);
		assertThat(JsonPath.<List<String>>read(alerts.body(), "$.items[*].type")).containsExactly("TANK_CAPACITY_EXCEEDED");
		String alert = JsonPath.read(alerts.body(), "$.items[0].id");

		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, new Scope(ScopeType.COMPANY, UUID.fromString(company)), false));
		assertThat(JsonPath.<List<String>>read(authed("GET", "/api/v1/company/alerts", manager.accessToken(), null).body(),
				"$.items[*].type")).containsExactly("TANK_CAPACITY_EXCEEDED");

		HttpResponse<String> acknowledged = authed("POST", "/api/v1/ops/alerts/" + alert + "/acknowledge", ops.accessToken(),
				null);
		assertThat(acknowledged.statusCode()).as(acknowledged.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(acknowledged.body(), "$.status")).isEqualTo("ACKNOWLEDGED");
	}

	@Test
	void aFuelingThatCrossesTheLowBalanceThresholdRaisesOneAlert() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany(50_000).toString();
		credit(company, 60_000);
		Car first = car(company);
		Car second = car(company);

		HttpResponse<String> low = fuel(pump, first, 10_000); // 60,000 → 45,000: below 50,000
		assertThat(JsonPath.<String>read(low.body(), "$.walletStatus")).isEqualTo("LOW");
		assertThat(status(fuel(pump, second, 10_000))).isEqualTo("COMPLETED"); // already low: no second alert
		assertThat(JsonPath.<List<String>>read(authed("GET", "/api/v1/ops/alerts?companyId=" + company + "&type=LOW_BALANCE",
				ops.accessToken(), null).body(), "$.items[*].type")).hasSize(1);
	}

	// --------------------------------------------------------------------------- who reads what

	@Test
	void eachAudienceSeesOnlyItsOwnFuelings() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String mine = seedCompany().toString();
		String theirs = seedCompany().toString();
		credit(mine, 100_000);
		credit(theirs, 100_000);
		String myFueling = JsonPath.read(fuel(pump, car(mine), 1_000).body(), "$.id");
		String theirFueling = JsonPath.read(fuel(pump, car(theirs), 1_000).body(), "$.id");

		SignedIn accountant = signIn(seed(Role.ACCOUNTANT, new Scope(ScopeType.COMPANY, UUID.fromString(mine)), false));
		List<String> seen = JsonPath.read(authed("GET", "/api/v1/company/fuelings", accountant.accessToken(), null).body(),
				"$.items[*].id");
		assertThat(seen).containsExactly(myFueling);
		assertThat(authed("GET", "/api/v1/company/fuelings/" + theirFueling, accountant.accessToken(), null).statusCode())
			.isEqualTo(404);

		SignedIn station = signIn(seed(Role.STATION_MANAGER,
				new Scope(ScopeType.OPERATOR, UUID.fromString(pump.operatorId())), false));
		List<String> atStation = JsonPath.read(authed("GET", "/api/v1/station/fuelings", station.accessToken(), null)
			.body(), "$.items[*].id");
		assertThat(atStation).containsExactlyInAnyOrder(myFueling, theirFueling);
		SignedIn otherStation = signIn(seed(Role.STATION_MANAGER));
		assertThat(authed("GET", "/api/v1/station/fuelings/" + myFueling, otherStation.accessToken(), null).statusCode())
			.isEqualTo(404);

		HttpResponse<String> detail = authed("GET", "/api/v1/ops/fuelings/" + myFueling, ops.accessToken(), null);
		assertThat(detail.statusCode()).isEqualTo(200);
		assertThat(detail.body()).as("no tag id or UID anywhere").doesNotContain("tagId").doesNotContain("tagUid");
	}

	@Test
	void theRuntimeRoleCannotRewriteAFueling() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		String id = JsonPath.read(fuel(pump, car(company), 1_000).body(), "$.id");
		try (var app = Owner.connect(); var sql = app.createStatement()) {
			sql.execute("set role zawed_app");
			org.assertj.core.api.Assertions
				.assertThatThrownBy(() -> sql.executeUpdate("update fuelings set volume_ml = 1 where id = '" + id + "'"))
				.hasMessageContaining("permission denied");
		}
	}

	@Test
	void oneClientTxnIdRacedOnTwoCompaniesRecordsOneFueling() throws Exception {
		// Two wallets, so the second request does not wait behind the first: it reaches the unique
		// index and must fall back to the stored outcome, which is for another tag.
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String first = seedCompany().toString();
		String second = seedCompany().toString();
		credit(first, 100_000);
		credit(second, 100_000);
		List<Car> cars = List.of(car(first), car(second));
		UUID txn = UUID.randomUUID();
		List<Integer> statuses = Concurrently.run(2, (i) -> fuel(pump, cars.get(i), txn, 10_000, null).statusCode());
		assertThat(statuses).containsExactlyInAnyOrder(201, 409);
		assertThat(Owner.<Long>queryOne("select count(*) from fuelings where client_txn_id = ?", txn)).isEqualTo(1L);
		assertThat(walletBalance(first) + walletBalance(second)).isEqualTo(200_000 - 15_000);
	}

	@Test
	void twoFuelingsOfOneTagAtOnceGiveOneCompletedAndOneCooldown() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		Car car = car(company);
		List<String> outcomes = Concurrently.run(2, (i) -> {
			HttpResponse<String> response = fuel(pump, car, 10_000);
			String status = JsonPath.read(response.body(), "$.status");
			return "COMPLETED".equals(status) ? status : JsonPath.read(response.body(), "$.declineReason");
		});
		assertThat(outcomes).containsExactlyInAnyOrder("COMPLETED", "COOLDOWN");
		assertThat(walletBalance(company)).isEqualTo(85_000);
	}

	@Test
	void suspendingTheOperatorStopsItsPumps() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		Car car = car(company);
		assertThat(authed("POST", "/api/v1/ops/operators/" + pump.operatorId() + "/status", ops.accessToken(),
				"{\"status\":\"SUSPENDED\",\"reason\":\"Contract ended\"}").statusCode()).isEqualTo(200);
		HttpResponse<String> refused = fuel(pump, car, 10_000);
		assertThat(refused.statusCode()).isEqualTo(401);
		assertThat(code(refused)).isEqualTo("DEVICE_UNAUTHENTICATED");
		assertThat(walletBalance(company)).isEqualTo(100_000);
		assertThat(code(tokenRequest(pump.deviceId(), pump.keys(), java.time.Instant.now().getEpochSecond(), nonce())))
			.isEqualTo("DEVICE_UNAUTHENTICATED");
	}

	@Test
	void aFuelingRowCannotBeChangedEvenByTheOwner() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		String id = JsonPath.read(fuel(pump, car(company), 1_000).body(), "$.id");
		org.assertj.core.api.Assertions
			.assertThatThrownBy(() -> Owner.update("update fuelings set volume_ml = 1 where id = ?::uuid", id))
			.hasMessageContaining("immutable");
		org.assertj.core.api.Assertions
			.assertThatThrownBy(() -> Owner.update("delete from fuelings where id = ?::uuid", id))
			.hasMessageContaining("immutable");
	}

	// --------------------------------------------------------------------------------- receipts

	@Test
	void aReceiptLinkOpensOnlyWithItsSignature() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		HttpResponse<String> done = fuel(pump, car(company), 10_000);
		String path = JsonPath.read(done.body(), "$.receiptPath");

		HttpResponse<String> receipt = get(path);
		assertThat(receipt.statusCode()).as(receipt.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(receipt.body(), "$.reference")).isEqualTo(JsonPath.read(done.body(), "$.reference"));
		assertThat(receipt.body()).doesNotContain(company).doesNotContain(pump.deviceId()).doesNotContain(pump.workerId());
		assertThat(JsonPath.<String>read(receipt.body(), "$.agentWorkerId"))
			.endsWith(pump.workerId().substring(pump.workerId().length() - 3));

		String id = JsonPath.read(done.body(), "$.id");
		assertThat(get("/api/v1/receipts/" + id).statusCode()).isEqualTo(404);
		assertThat(get("/api/v1/receipts/" + id + "?sig=forged").statusCode()).isEqualTo(404);
		String sig = path.substring(path.indexOf("sig=") + 4);
		assertThat(get("/api/v1/receipts/" + UUID.randomUUID() + "?sig=" + sig).statusCode()).isEqualTo(404);
	}

	// ----------------------------------------------------------------------------------- helpers

	private static String status(HttpResponse<String> response) {
		assertThat(response.statusCode()).as(response.body()).isEqualTo(201);
		return JsonPath.read(response.body(), "$.status");
	}

	private static String reason(HttpResponse<String> response) {
		assertThat(status(response)).isEqualTo("DECLINED");
		return JsonPath.read(response.body(), "$.declineReason");
	}

	private static long walletBalance(String company) {
		return Owner.<Long>queryOne("select balance_piastres from company_wallets where company_id = ?::uuid", company);
	}

}
