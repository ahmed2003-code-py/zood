package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.sql.Timestamp;
import java.time.Duration;
import java.time.Instant;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.fueling.Fuelings.Conflict;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;
import com.zawed.api.rules.FuelingRules.DeclineReason;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * Offline fuelings sent in batches (B5 contract §3 and §4) through the real POS API: every capture
 * is recorded once and never declined, a failing check becomes a conflict and an alert, the wallet
 * stops at zero and the rest becomes the company's debt, and an item that cannot be recorded is
 * refused out loud without holding back the others.
 */
class OfflineSyncTests extends OfflineTestSupport {

	// ------------------------------------------------------------------------ a batch, and again

	@Test
	void aBatchIsRecordedOnceAndSendingItAgainChangesNothing() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		List<Capture> batch = List.of(capture(pump, car(company), 10_000), capture(pump, car(company), 20_000),
				capture(pump, car(company), 4_000));
		UUID batchId = UUID.randomUUID();

		HttpResponse<String> first = sync(pump, batchId, 7, batch);
		assertThat(results(first)).containsExactly("RECORDED", "RECORDED", "RECORDED");
		assertThat(first.headers().firstValue("Cache-Control").orElse("")).contains("no-store");
		assertThat(JsonPath.<String>read(first.body(), "$.batchId")).isEqualTo(batchId.toString());
		assertThat(JsonPath.<List<String>>read(first.body(), "$.results[*].clientTxnId")).as("in the order sent")
			.containsExactly(batch.get(0).clientTxnId().toString(), batch.get(1).clientTxnId().toString(),
					batch.get(2).clientTxnId().toString());
		assertThat(JsonPath.<List<Integer>>read(first.body(), "$.results[*].fueling.amountPiastres"))
			.containsExactly(15_000, 30_000, 6_000);
		assertThat(conflicts(first, 0)).isEmpty();
		assertThat(first.body()).as("a device is never told a balance").doesNotContain("balance").doesNotContain("49000");

		// Each is a completed offline fueling, judged at the device's time, by the agent the item names.
		Map<String, Object> row = fuelingRow(fuelingId(first, 0));
		assertThat(row.get("status")).isEqualTo("COMPLETED");
		assertThat(row.get("origin")).isEqualTo("OFFLINE");
		assertThat(row.get("agent_worker_id")).isEqualTo(pump.workerId());
		assertThat(((Timestamp) row.get("evaluated_at")).toInstant()).isEqualTo(((Timestamp) row.get("device_at")).toInstant());
		assertThat(((Timestamp) row.get("evaluated_at")).toInstant())
			.isCloseTo(batch.get(0).deviceAt(), org.assertj.core.api.Assertions.within(1, java.time.temporal.ChronoUnit.MILLIS));
		// The money: wallet to station payable, once per fueling; the cooldown starts at the device's time.
		assertThat(walletBalance(company)).isEqualTo(100_000 - 51_000);
		assertThat(walletLedger(company)).isEqualTo(walletBalance(company));
		assertThat(stationPayable(pump.stationId())).isEqualTo(51_000);
		assertThat(owed(company)).isZero();
		Instant cooldown = Owner.<Timestamp>queryOne("select cooldown_until from rfid_tags where id = ?", row.get("tag_id"))
			.toInstant();
		assertThat(Duration.between(batch.get(0).deviceAt(), cooldown)).isCloseTo(Duration.ofMinutes(180), Duration.ofSeconds(1));
		// The receipt link of a synced fueling opens like any other.
		assertThat(get(JsonPath.<String>read(first.body(), "$.results[0].fueling.receiptPath")).statusCode()).isEqualTo(200);

		// The same batch again: every item is a duplicate carrying the stored fueling; nothing moves.
		HttpResponse<String> again = sync(pump, batchId, 7, batch);
		assertThat(results(again)).containsExactly("DUPLICATE", "DUPLICATE", "DUPLICATE");
		assertThat(JsonPath.<List<String>>read(again.body(), "$.results[*].fueling.id"))
			.isEqualTo(JsonPath.<List<String>>read(first.body(), "$.results[*].fueling.id"));
		assertThat(walletBalance(company)).isEqualTo(49_000);
		assertThat(stationPayable(pump.stationId())).isEqualTo(51_000);
		assertThat(count("select count(*) from fuelings where company_id = ?::uuid", company)).isEqualTo(3);
		assertThat(count("select count(*) from ledger_transactions where source_type = 'FUELING' and company_id = ?::uuid",
				company)).isEqualTo(3);
		assertThat(count("select count(*) from alerts where company_id = ?::uuid or device_id = ?::uuid", company,
				pump.deviceId())).as("nothing to flag").isZero();

		// Both requests are in the sync monitor's log, with what each did.
		List<Map<String, Object>> batches = batchRows(pump);
		assertThat(batches).hasSize(2);
		assertThat(batches.get(0)).containsEntry("device_batch_id", batchId)
			.containsEntry("item_count", 3)
			.containsEntry("recorded", 3)
			.containsEntry("duplicates", 0)
			.containsEntry("rejected", 0)
			.containsEntry("with_conflicts", 0)
			.containsEntry("pending_reported", 7);
		assertThat(batches.get(1)).containsEntry("recorded", 0).containsEntry("duplicates", 3);
		assertThat(batches.get(1).get("operator_id")).hasToString(pump.operatorId());
	}

	@Test
	void theSameBatchFromTwoThreadsAtOnceRecordsEachFuelingOnce() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String first = seedCompany().toString();
		String second = seedCompany().toString();
		credit(first, 1_000_000);
		credit(second, 1_000_000);
		List<Capture> batch = new ArrayList<>();
		for (int i = 0; i < 12; i++) {
			batch.add(capture(pump, car(i % 3 == 0 ? second : first), 10_000));
		}
		UUID batchId = UUID.randomUUID();

		List<HttpResponse<String>> answers = Concurrently.run(4, (i) -> sync(pump, batchId, 0, batch));

		for (int item = 0; item < batch.size(); item++) {
			List<String> outcomes = new ArrayList<>();
			for (HttpResponse<String> answer : answers) {
				outcomes.add(results(answer).get(item));
			}
			assertThat(outcomes).as("item %d", item).containsExactlyInAnyOrder("RECORDED", "DUPLICATE", "DUPLICATE", "DUPLICATE");
			assertThat(count("select count(*) from fuelings where client_txn_id = ?", batch.get(item).clientTxnId())).isEqualTo(1);
		}
		assertThat(walletBalance(first)).isEqualTo(1_000_000 - 8 * 15_000);
		assertThat(walletBalance(second)).isEqualTo(1_000_000 - 4 * 15_000);
		assertThat(walletLedger(first)).isEqualTo(walletBalance(first));
		assertThat(stationPayable(pump.stationId())).isEqualTo(12 * 15_000);
		assertThat(count("select count(*) from ledger_transactions t join fuelings f on f.id = t.source_id "
				+ "where t.source_type = 'FUELING' and f.device_id = ?::uuid", pump.deviceId())).isEqualTo(12);
	}

	// ------------------------------------------------------------------------------- conflicts

	@Test
	void everyFailingCheckIsRecordedAsAConflictAndAlertedWhileTheFuelingStaysCompleted() throws Exception {
		Pump pump = pump("GAS_92", "DIESEL");
		price(pump.stationId(), "GAS_92", PRICE);
		price(pump.stationId(), "DIESEL", 1_200);
		String company = seedCompany().toString();
		credit(company, 10_000_000);

		// COOLDOWN: the car fuelled online a moment before the capture.
		Car cooled = car(company);
		assertThat(fuel(pump, cooled, 1_000).statusCode()).isEqualTo(201);
		assertRecordedWith(pump, capture(pump, cooled, 1_000), "WARNING", "COOLDOWN");

		// TAG_INACTIVE: reported lost, and still used at the pump.
		Car lost = car(company);
		String tag = Owner.<UUID>queryOne("select id from rfid_tags where vehicle_id = ?::uuid", lost.vehicleId()).toString();
		assertThat(authed("POST", "/api/v1/ops/tags/" + tag + "/status", ops.accessToken(),
				"{\"status\":\"LOST\",\"reason\":\"Driver lost it\"}").statusCode()).isEqualTo(200);
		assertRecordedWith(pump, capture(pump, lost, 1_000), "CRITICAL", "TAG_INACTIVE");

		// VEHICLE_INACTIVE.
		Car suspended = car(company);
		assertThat(authed("POST", "/api/v1/ops/vehicles/" + suspended.vehicleId() + "/status", ops.accessToken(),
				"{\"status\":\"SUSPENDED\",\"reason\":\"Workshop\"}").statusCode()).isEqualTo(200);
		assertRecordedWith(pump, capture(pump, suspended, 1_000), "CRITICAL", "VEHICLE_INACTIVE");

		// FUEL_TYPE_MISMATCH: diesel into a petrol car; charged at the price of what was dispensed.
		HttpResponse<String> wrongFuel = assertRecordedWith(pump, capture(pump, car(company), 1_000).fuel("DIESEL"), "WARNING",
				"FUEL_TYPE_MISMATCH");
		assertThat(JsonPath.<Integer>read(wrongFuel.body(), "$.results[0].fueling.unitPricePiastres")).isEqualTo(1_200);

		// LIMIT_EXCEEDED.
		Car limited = car(company);
		assertThat(authed("PUT", "/api/v1/ops/vehicles/" + limited.vehicleId() + "/limits", ops.accessToken(),
				"{\"kind\":\"VOLUME\",\"period\":\"DAY\",\"maxValue\":5000}").statusCode()).isEqualTo(200);
		assertRecordedWith(pump, capture(pump, limited, 6_000), "WARNING", "LIMIT_EXCEEDED");

		// STALE_CAPTURE: a capture that sat on the device for eight days, from a shift of that day.
		Instant longAgo = Instant.now().minus(Duration.ofDays(8));
		pastShift(pump, longAgo.minus(Duration.ofHours(1)), longAgo.plus(Duration.ofHours(1)));
		priceSince(pump.stationId(), "GAS_92", 1_400, longAgo.minus(Duration.ofDays(1)));
		HttpResponse<String> stale = assertRecordedWith(pump, capture(pump, car(company), 1_000).at(longAgo), "WARNING",
				"STALE_CAPTURE");
		assertThat(JsonPath.<Integer>read(stale.body(), "$.results[0].fueling.unitPricePiastres")).as("the price of that day")
			.isEqualTo(1_400);

		// Everything above was charged in full: a conflict never stops the money.
		assertThat(walletBalance(company)).isEqualTo(10_000_000 - (1_500 + 1_500 + 1_500 + 1_500 + 1_200 + 9_000 + 1_400));
		assertThat(walletLedger(company)).isEqualTo(walletBalance(company));

		// FUEL_NOT_AVAILABLE: a fuel this station has no price for. Recorded with no amount, flagged.
		Pump petrolOnly = pump("GAS_92");
		price(petrolOnly.stationId(), "GAS_92", PRICE);
		Car diesel = car(company, "DIESEL", "NORMAL", null, 80_000);
		long before = walletBalance(company);
		HttpResponse<String> unpriced = assertRecordedWith(petrolOnly, capture(petrolOnly, diesel, 1_000), "CRITICAL",
				"FUEL_NOT_AVAILABLE");
		assertThat(JsonPath.<Integer>read(unpriced.body(), "$.results[0].fueling.amountPiastres")).isZero();
		assertThat(fuelingRow(fuelingId(unpriced, 0)).get("ledger_transaction_id")).isNull();
		assertThat(walletBalance(company)).isEqualTo(before);

		// COMPANY_FROZEN, and several at once: all of them, in the order of the checks, in one alert.
		String frozen = seedCompany().toString();
		credit(frozen, 100_000);
		Car iced = car(frozen);
		String icedTag = Owner.<UUID>queryOne("select id from rfid_tags where vehicle_id = ?::uuid", iced.vehicleId()).toString();
		assertThat(authed("POST", "/api/v1/ops/companies/" + frozen + "/status", ops.accessToken(),
				"{\"status\":\"FROZEN\",\"reason\":\"Unpaid\"}").statusCode()).isEqualTo(200);
		assertRecordedWith(pump, capture(pump, iced, 1_000), "CRITICAL", "COMPANY_FROZEN");
		assertThat(walletBalance(frozen)).as("a frozen company still pays for the fuel it took").isEqualTo(100_000 - 1_500);
		assertThat(authed("POST", "/api/v1/ops/tags/" + icedTag + "/status", ops.accessToken(),
				"{\"status\":\"STOLEN\",\"reason\":\"Reported\"}").statusCode()).isEqualTo(200);
		assertRecordedWith(pump, capture(pump, iced, 2_000), "CRITICAL", "COOLDOWN", "TAG_INACTIVE", "COMPANY_FROZEN");

		// The company reads its own conflicts; the batch log counted them.
		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, new Scope(ScopeType.COMPANY, UUID.fromString(frozen)), false));
		assertThat(JsonPath.<List<String>>read(authed("GET", "/api/v1/company/alerts", manager.accessToken(), null).body(),
				"$.items[*].type")).containsExactly("SYNC_CONFLICT", "SYNC_CONFLICT");
		assertThat(batchRows(pump)).allSatisfy((batch) -> assertThat(batch).containsEntry("recorded", 1)
			.containsEntry("with_conflicts", 1));
		assertThat(count("select count(*) from fuelings where origin = 'OFFLINE' and status <> 'COMPLETED'")).isZero();
	}

	@Test
	void everyDeclineReasonHasItsConflict() {
		for (DeclineReason reason : DeclineReason.values()) {
			assertThat(Conflict.of(reason).name()).isEqualTo(reason.name());
		}
		// And the database knows every one (the CHECK on fuelings.conflicts, V8).
		for (Conflict conflict : Conflict.values()) {
			assertThat(Owner.<Boolean>queryOne("""
					select pg_get_constraintdef(oid) like ? from pg_constraint where conname = 'fuelings_conflicts_known'
					""", "%'" + conflict.name() + "'%")).as(conflict.name()).isTrue();
		}
	}

	// ----------------------------------------------------------------- the wallet and the debt

	@Test
	void whatTheWalletCannotCoverBecomesTheCompanysDebtAndTheStationIsPaidInFull() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany(5_000).toString();
		credit(company, 10_000);

		// 20 L × 15.00 = 300.00 EGP against a wallet of 100.00 EGP.
		HttpResponse<String> short200 = sync(pump, capture(pump, car(company), 20_000));
		assertThat(conflicts(short200, 0)).containsExactly("INSUFFICIENT_BALANCE");
		String fueling = fuelingId(short200, 0);
		assertThat(JsonPath.<Integer>read(short200.body(), "$.results[0].fueling.amountPiastres")).isEqualTo(30_000);

		assertThat(walletBalance(company)).as("exactly zero, never below").isZero();
		assertThat(walletLedger(company)).as("the cached balance is the ledger's").isZero();
		assertThat(owed(company)).isEqualTo(20_000);
		assertThat(stationPayable(pump.stationId())).as("the station is paid in full").isEqualTo(30_000);
		// One balanced posting for the fueling: wallet 100 + receivable 200 = station 300.
		UUID txn = (UUID) fuelingRow(fueling).get("ledger_transaction_id");
		assertThat(entries(txn)).containsExactlyInAnyOrder("DEBIT company:" + company + ":wallet 10000",
				"DEBIT company:" + company + ":receivable 20000", "CREDIT station:" + pump.stationId() + ":payable 30000");
		assertThat(Owner.<UUID>queryOne("select source_id from ledger_transactions where id = ?", txn)).hasToString(fueling);

		// Alerted to Operations and the company with what is owed; the debt is in the audit trail;
		// draining the wallet crossed its threshold like any other debit.
		HttpResponse<String> alerts = authed("GET", "/api/v1/ops/alerts?companyId=" + company, ops.accessToken(), null);
		assertThat(JsonPath.<List<String>>read(alerts.body(), "$.items[*].type")).containsExactlyInAnyOrder("SYNC_CONFLICT",
				"LOW_BALANCE");
		assertThat(JsonPath.<List<Integer>>read(alerts.body(), "$.items[?(@.type == 'SYNC_CONFLICT')].details.owedPiastres"))
			.containsExactly(20_000);
		assertThat(JsonPath.<List<String>>read(alerts.body(), "$.items[?(@.type == 'SYNC_CONFLICT')].severity"))
			.containsExactly("CRITICAL");
		assertThat(Owner.<String>queryOne("""
				select metadata ->> 'amountPiastres' from audit_log
				where action = 'COMPANY_RECEIVABLE_RAISED' and entity_id = ?::uuid
				""", company)).isEqualTo("20000");

		// With nothing left, the next capture is owed whole; the wallet row is not touched.
		HttpResponse<String> allOwed = sync(pump, capture(pump, car(company), 10_000));
		assertThat(conflicts(allOwed, 0)).containsExactly("INSUFFICIENT_BALANCE");
		assertThat(walletBalance(company)).isZero();
		assertThat(owed(company)).isEqualTo(35_000);
		assertThat(stationPayable(pump.stationId())).isEqualTo(45_000);
		assertThat(entries((UUID) fuelingRow(fuelingId(allOwed, 0)).get("ledger_transaction_id"))).containsExactlyInAnyOrder(
				"DEBIT company:" + company + ":receivable 15000", "CREDIT station:" + pump.stationId() + ":payable 15000");

		// A top-up fills the wallet; the debt stays where it is until someone decides how it is settled.
		credit(company, 50_000);
		assertThat(walletBalance(company)).isEqualTo(50_000);
		assertThat(owed(company)).isEqualTo(35_000);

		// The nightly comparison of wallets with their ledger still finds nothing for this company.
		SignedIn admin = signIn(seed(Role.SUPER_ADMIN));
		HttpResponse<String> run = authed("POST", "/api/v1/ops/reconciliation/runs", admin.accessToken(), null);
		assertThat(run.statusCode()).as(run.body()).isEqualTo(201);
		assertThat(count("select count(*) from reconciliation_findings where run_id = ?::uuid and company_id = ?::uuid",
				JsonPath.<String>read(run.body(), "$.id"), company)).isZero();
	}

	@Test
	void anOnlineFuelingAndAnOfflineBatchAtOnceNeverOverdrawTheWallet() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		long opening = 15_000 * 10;
		credit(company, opening); // ten 10 L fuelings; twenty are coming
		List<Car> online = new ArrayList<>();
		List<Capture> batch = new ArrayList<>();
		for (int i = 0; i < 10; i++) {
			online.add(car(company));
			batch.add(capture(pump, car(company), 10_000));
		}

		List<String> outcomes = Concurrently.run(11, (i) -> {
			if (i == 0) {
				assertThat(results(sync(pump, UUID.randomUUID(), 0, batch))).containsOnly("RECORDED");
				return "BATCH";
			}
			HttpResponse<String> response = fuel(pump, online.get(i - 1), 10_000);
			assertThat(response.statusCode()).as(response.body()).isEqualTo(201);
			String status = JsonPath.read(response.body(), "$.status");
			return "COMPLETED".equals(status) ? status : JsonPath.read(response.body(), "$.declineReason");
		});

		long completedOnline = outcomes.stream().filter("COMPLETED"::equals).count();
		// Which side wins the race decides how many online fuelings are declined (none, when all ten
		// land before the batch); whatever is declined is declined for the balance, never owed.
		assertThat(outcomes.stream().filter((o) -> !"BATCH".equals(o) && !"COMPLETED".equals(o)))
			.as("an online fueling the wallet cannot cover is declined, never owed")
			.allMatch("INSUFFICIENT_BALANCE"::equals);
		long charged = 15_000 * (10 + completedOnline);

		long wallet = walletBalance(company);
		assertThat(wallet).isGreaterThanOrEqualTo(0);
		assertThat(walletLedger(company)).as("the cached balance is the ledger's").isEqualTo(wallet);
		assertThat(stationPayable(pump.stationId())).as("every recorded fueling is paid to the station").isEqualTo(charged);
		assertThat((opening - wallet) + owed(company)).as("paid from the wallet plus owed = charged").isEqualTo(charged);
		assertThat(owed(company)).as("a debt arises only once the wallet is empty")
			.isEqualTo(Math.max(0, charged - opening));
		// The running balance on the wallet's entries never dipped below zero on the way.
		assertThat(count("""
				select count(*) from ledger_entries e join ledger_accounts a on a.id = e.account_id
				where a.code = ? and e.balance_after_piastres < 0
				""", "company:" + company + ":wallet")).isZero();
		assertThat(count("select count(*) from fuelings where company_id = ?::uuid and origin = 'OFFLINE' and status = 'COMPLETED'",
				company)).isEqualTo(10);
	}

	// ------------------------------------------------------------------- judged at its own time

	@Test
	void aCaptureIsJudgedAtItsOwnTimeNotAtTheTimeItArrives() throws Exception {
		Pump pump = pump();
		String company = seedCompany().toString();
		credit(company, 1_000_000);
		Car car = car(company);
		assertThat(authed("PUT", "/api/v1/ops/vehicles/" + car.vehicleId() + "/limits", ops.accessToken(),
				"{\"kind\":\"VOLUME\",\"period\":\"DAY\",\"maxValue\":10000}").statusCode()).isEqualTo(200);
		// Yesterday in the station's zone: a shift from 09:00 to 16:00, at yesterday's price.
		ZoneId cairo = ZoneId.of("Africa/Cairo");
		LocalDate yesterday = LocalDate.now(cairo).minusDays(1);
		Instant morning = yesterday.atTime(10, 0).atZone(cairo).toInstant();
		Instant afternoon = yesterday.atTime(15, 0).atZone(cairo).toInstant();
		pastShift(pump, morning.minus(Duration.ofHours(1)), afternoon.plus(Duration.ofHours(1)));
		priceSince(pump.stationId(), "GAS_92", 1_000, morning.minus(Duration.ofDays(30)));
		price(pump.stationId(), "GAS_92", PRICE); // today's price, in force from now

		// Today the car fuels online: 8 L of its 10 L a day, at today's price.
		HttpResponse<String> today = fuel(pump, car, 8_000);
		assertThat(JsonPath.<String>read(today.body(), "$.status")).isEqualTo("COMPLETED");
		Instant cooldownToday = Instant.parse(JsonPath.<String>read(today.body(), "$.cooldownUntil"));

		// Yesterday morning's 8 L arrives now. It is counted in yesterday, priced at yesterday's
		// price, and is not in today's cooldown: no conflict, although today already holds 8 L.
		HttpResponse<String> first = sync(pump, capture(pump, car, 8_000).at(morning));
		assertThat(conflicts(first, 0)).isEmpty();
		assertThat(JsonPath.<Integer>read(first.body(), "$.results[0].fueling.unitPricePiastres")).isEqualTo(1_000);
		assertThat(JsonPath.<Integer>read(first.body(), "$.results[0].fueling.amountPiastres")).isEqualTo(8_000);

		// Yesterday afternoon's 5 L makes yesterday 13 L: over yesterday's limit, and only that.
		HttpResponse<String> second = sync(pump, capture(pump, car, 5_000).at(afternoon));
		assertThat(conflicts(second, 0)).containsExactly("LIMIT_EXCEEDED");

		// A capture two hours after the morning one is inside its cooldown, whichever arrived last.
		HttpResponse<String> tooSoon = sync(pump, capture(pump, car, 1_000).at(morning.plus(Duration.ofHours(2))));
		assertThat(conflicts(tooSoon, 0)).containsExactly("COOLDOWN", "LIMIT_EXCEEDED");

		// Old captures never shorten the cooldown today's fueling started.
		assertThat(Owner.<Timestamp>queryOne("select cooldown_until from rfid_tags where vehicle_id = ?::uuid", car.vehicleId())
			.toInstant()).isCloseTo(cooldownToday, org.assertj.core.api.Assertions.within(1, java.time.temporal.ChronoUnit.MILLIS));
		assertThat(walletBalance(company)).isEqualTo(1_000_000 - 12_000 - 8_000 - 5_000 - 1_000);
	}

	// ------------------------------------------------------------------------------- rejections

	@Test
	void aCaptureCountsOnlyForAnAgentWhoWasSignedInOnThatDeviceThen() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		Car car = car(company);

		// An agent of another station; an agent of this station who never signed in on this device.
		Pump elsewhere = pump();
		String[] colleague = agent(pump.stationId());
		String colleagueId = Owner.<UUID>queryOne("select id from agents where worker_id = ?", colleague[0]).toString();
		// The right agent, but an hour before the shift began.
		HttpResponse<String> refused = sync(pump, capture(pump, car, 1_000).by(agentId(elsewhere)),
				capture(pump, car, 1_000).by(colleagueId), capture(pump, car, 1_000).by(UUID.randomUUID().toString()),
				capture(pump, car, 1_000).at(Instant.now().minus(Duration.ofHours(1))));
		// Not of this station (another station's agent, an unknown id): nothing can be recorded. Of this
		// station but with no session on this device then: recorded, flagged AGENT_UNVERIFIED (B5 review 1).
		assertThat(results(refused)).containsExactly("REJECTED", "RECORDED", "REJECTED", "RECORDED");
		assertThat(rejection(refused, 0)).isEqualTo("AGENT_NOT_OF_STATION");
		assertThat(rejection(refused, 2)).isEqualTo("AGENT_NOT_OF_STATION");
		assertThat(conflicts(refused, 1)).contains("AGENT_UNVERIFIED");
		assertThat(conflicts(refused, 3)).contains("AGENT_UNVERIFIED");
		assertThat(count("select count(*) from fuelings where company_id = ?::uuid", company)).isEqualTo(2);
		assertThat(count("select count(*) from alerts where type = 'SYNC_REJECTED' and device_id = ?::uuid", pump.deviceId()))
			.isEqualTo(2);

		// A shift that ended an hour ago: what was captured in it counts, what is dated after it does not.
		Instant now = Instant.now();
		Owner.update("""
				update agent_sessions set created_at = ?, revoked_at = ?, revoke_reason = 'SIGNED_OUT'
				where device_id = ?::uuid and revoked_at is null
				""", Timestamp.from(now.minus(Duration.ofHours(3))), Timestamp.from(now.minus(Duration.ofHours(1))),
				pump.deviceId());
		priceSince(pump.stationId(), "GAS_92", 1_300, now.minus(Duration.ofDays(1)));
		Capture during = capture(pump, car, 2_000).at(now.minus(Duration.ofHours(2)));
		Capture after = capture(pump, car(company), 2_000).at(now.minus(Duration.ofMinutes(30)));
		// Sent while a colleague is signed in on the device: the capture is still its own agent's.
		String colleagueSession = JsonPath.read(agentLogin(pump.deviceToken(), colleague[0], colleague[1]).body(),
				"$.sessionToken");
		HttpResponse<String> mixed = pos("POST", "/api/v1/pos/sync", pump.deviceToken(), colleagueSession,
				batchBody(UUID.randomUUID(), 0, List.of(during, after)));
		assertThat(results(mixed)).containsExactly("RECORDED", "RECORDED");
		assertThat(conflicts(mixed, 1)).as("dated after the shift ended").contains("AGENT_UNVERIFIED");
		assertThat(fuelingRow(fuelingId(mixed, 0)).get("agent_worker_id")).isEqualTo(pump.workerId());
		assertThat(fuelingRow(fuelingId(mixed, 0)).get("agent_id")).hasToString(agentId(pump));
	}

	@Test
	void anUnknownTagIsRejectedAndOperationsIsToldWithoutTheUidBeingKeptAnywhere() throws Exception {
		Pump pump = pump();
		String uid = randomUid();
		Capture capture = new Capture(UUID.randomUUID(), uid, 30_000, null, Instant.now(), agentId(pump));
		UUID batchId = UUID.randomUUID();

		HttpResponse<String> refused = sync(pump, batchId, 0, List.of(capture));
		assertThat(rejection(refused, 0)).isEqualTo("TAG_NOT_RECOGNISED");
		assertThat(JsonPath.<Object>read(refused.body(), "$.results[0].fueling")).isNull();
		assertThat(count("select count(*) from fuelings where client_txn_id = ?", capture.clientTxnId())).isZero();

		// One critical alert, for Operations only, naming the device and the capture.
		HttpResponse<String> alerts = authed("GET", "/api/v1/ops/alerts?type=SYNC_REJECTED&limit=100", ops.accessToken(), null);
		List<Map<String, Object>> mine = JsonPath.read(alerts.body(), "$.items[?(@.deviceId == '" + pump.deviceId() + "')]");
		assertThat(mine).hasSize(1);
		assertThat(mine.getFirst()).containsEntry("severity", "CRITICAL")
			.containsEntry("stationId", pump.stationId())
			.containsEntry("operatorId", pump.operatorId());
		assertThat(mine.getFirst().get("companyId")).isNull();
		@SuppressWarnings("unchecked")
		Map<String, Object> details = (Map<String, Object>) mine.getFirst().get("details");
		assertThat(details).containsEntry("code", "TAG_NOT_RECOGNISED")
			.containsEntry("clientTxnId", capture.clientTxnId().toString())
			.containsEntry("uidLast4", uid.substring(uid.length() - 4))
			.containsEntry("deviceCode", pump.deviceCode())
			.containsEntry("batchId", batchId.toString())
			.containsEntry("volumeMl", 30_000);
		assertThat(Owner.<Boolean>queryOne("select company_visible from alerts where device_id = ?::uuid", pump.deviceId()))
			.isFalse();

		// The UID itself is in no alert and no audit row, in either case.
		assertThat(count("select count(*) from alerts where details::text ilike ?", "%" + uid + "%")).isZero();
		assertThat(count("select count(*) from audit_log where metadata::text ilike ?", "%" + uid + "%")).isZero();

		// Sent again (the answer was lost): rejected again, and Operations is not told twice.
		assertThat(rejection(sync(pump, batchId, 0, List.of(capture)), 0)).isEqualTo("TAG_NOT_RECOGNISED");
		assertThat(count("select count(*) from alerts where type = 'SYNC_REJECTED' and device_id = ?::uuid", pump.deviceId()))
			.isEqualTo(1);
		assertThat(batchRows(pump)).hasSize(2).allSatisfy((batch) -> assertThat(batch).containsEntry("rejected", 1));

		// A string that is not a UID at all leaves nothing of itself behind.
		assertThat(rejection(sync(pump, new Capture(UUID.randomUUID(), "not-a-uid-QQ77", 1_000, null, Instant.now(),
				agentId(pump))), 0)).isEqualTo("TAG_NOT_RECOGNISED");
		assertThat(count("select count(*) from alerts where details::text ilike '%QQ77%'")).isZero();
	}

	@Test
	void oneItemThatCannotBeRecordedDoesNotHoldBackTheOthers() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		Capture good = capture(pump, car(company), 10_000);
		Capture unknownTag = new Capture(UUID.randomUUID(), randomUid(), 10_000, null, Instant.now(), agentId(pump));
		Capture tooMuch = capture(pump, car(company), FuelingEngine.MAX_VOLUME_ML + 1);
		Capture nothing = capture(pump, car(company), 0);
		Capture fromTheFuture = capture(pump, car(company), 10_000).at(Instant.now().plus(Duration.ofMinutes(10)));
		Capture alsoGood = capture(pump, car(company), 20_000);

		HttpResponse<String> answer = sync(pump, good, unknownTag, tooMuch, nothing, fromTheFuture, alsoGood);
		assertThat(results(answer)).containsExactly("RECORDED", "REJECTED", "REJECTED", "REJECTED", "RECORDED", "RECORDED");
		assertThat(rejection(answer, 1)).isEqualTo("TAG_NOT_RECOGNISED");
		assertThat(rejection(answer, 2)).isEqualTo("INVALID_VOLUME");
		assertThat(rejection(answer, 3)).isEqualTo("INVALID_VOLUME");
		// Dated ten minutes ahead: the fuel left the pump, so it is recorded, judged at arrival, flagged.
		assertThat(conflicts(answer, 4)).contains("DEVICE_CLOCK_SKEW");

		assertThat(walletBalance(company)).isEqualTo(100_000 - 15_000 - 15_000 - 30_000);
		assertThat(count("select count(*) from fuelings where company_id = ?::uuid", company)).isEqualTo(3);
		assertThat(batchRows(pump).getFirst()).containsEntry("item_count", 6)
			.containsEntry("recorded", 3)
			.containsEntry("rejected", 3)
			.containsEntry("duplicates", 0);
		assertThat(count("select count(*) from alerts where type = 'SYNC_REJECTED' and device_id = ?::uuid", pump.deviceId()))
			.isEqualTo(3);
	}

	@Test
	void aTransactionIdBelongsToOneDeviceAndOneFueling() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		Car car = car(company);
		Capture capture = capture(pump, car, 10_000);
		assertThat(results(sync(pump, capture))).containsExactly("RECORDED");

		// The same id for another volume or another tag is not that fueling.
		HttpResponse<String> changed = sync(pump, capture.volume(12_000),
				new Capture(capture.clientTxnId(), car(company).tagUid(), 10_000, null, capture.deviceAt(), capture.agentId()));
		assertThat(rejection(changed, 0)).isEqualTo("IDEMPOTENCY_MISMATCH");
		assertThat(rejection(changed, 1)).isEqualTo("IDEMPOTENCY_MISMATCH");
		// The same id from another device is not this device's fueling.
		Pump other = pumpAt(pump.operatorId(), pump.stationId());
		assertThat(rejection(sync(other, new Capture(capture.clientTxnId(), car.tagUid(), 10_000, null, Instant.now(),
				agentId(other))), 0)).isEqualTo("CLIENT_TXN_CONFLICT");
		// An online retry of a synced capture gets the stored fueling, as any replay does.
		HttpResponse<String> replay = fuel(pump, car, capture.clientTxnId(), 10_000, null);
		assertThat(replay.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<Boolean>read(replay.body(), "$.replayed")).isTrue();

		assertThat(count("select count(*) from fuelings where client_txn_id = ?", capture.clientTxnId())).isEqualTo(1);
		assertThat(walletBalance(company)).isEqualTo(85_000);
	}

	@Test
	void anOnlineAttemptThatFellBackToTheQueueIsNeverChargedTwiceAndNeverLostSilently() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 20_000);
		Car car = car(company);

		// The terminal sent a fueling online, got no answer, and queued it under the same id.
		// 1. The server had completed it: the queue item is a duplicate and is charged once.
		UUID completed = UUID.randomUUID();
		assertThat(JsonPath.<String>read(fuel(pump, car, completed, 10_000, null).body(), "$.status")).isEqualTo("COMPLETED");
		HttpResponse<String> duplicate = sync(pump, new Capture(completed, car.tagUid(), 10_000, null, Instant.now(), agentId(pump)));
		assertThat(results(duplicate)).containsExactly("DUPLICATE");
		assertThat(JsonPath.<String>read(duplicate.body(), "$.results[0].fueling.origin")).isEqualTo("ONLINE");
		assertThat(walletBalance(company)).isEqualTo(5_000);

		// 2. The server had declined it (the wallet could not cover it) and the terminal let the fuel
		//    go on its offline rules. The id's outcome stays the decline, and the dispensed fuel is
		//    recorded too, as an offline fueling of its own, flagged DECLINED_ONLINE (B5 review 1).
		Car second = car(company);
		UUID declined = UUID.randomUUID();
		HttpResponse<String> online = fuel(pump, second, declined, 10_000, null);
		assertThat(JsonPath.<String>read(online.body(), "$.declineReason")).isEqualTo("INSUFFICIENT_BALANCE");
		Capture letGo = new Capture(declined, second.tagUid(), 10_000, null, Instant.now(), agentId(pump));
		HttpResponse<String> queued = sync(pump, letGo);
		assertThat(results(queued)).containsExactly("RECORDED");
		assertThat(conflicts(queued, 0)).contains("DECLINED_ONLINE", "INSUFFICIENT_BALANCE");
		assertThat(Owner.<String>queryOne("select status from fuelings where client_txn_id = ?", declined)).isEqualTo("DECLINED");
		assertThat(walletBalance(company)).isZero();
		assertThat(owed(company)).as("15,000 charged, 5,000 from the wallet").isEqualTo(10_000);
		// Sent again: found under the derived id, nothing moves.
		assertThat(results(sync(pump, letGo))).containsExactly("DUPLICATE");
		assertThat(owed(company)).isEqualTo(10_000);
	}

	@Test
	void anOnlineFuelingWhoseAnswerWasLostIsResolvedAndAMismatchIsReportedOnce() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		UUID charged = UUID.randomUUID();
		assertThat(JsonPath.<String>read(fuel(pump, car(company), charged, 10_000, null).body(), "$.status")).isEqualTo("COMPLETED");

		// The terminal never saw the answer and told the agent the fueling was refused.
		String told = "{\"toldOutcome\":\"REFUSED\"}";
		for (int i = 0; i < 2; i++) {
			HttpResponse<String> resolved = pos("POST", "/api/v1/pos/fuelings/" + charged + "/resolve", pump.deviceToken(),
					pump.session(), told);
			assertThat(resolved.statusCode()).as(resolved.body()).isEqualTo(200);
			assertThat(JsonPath.<String>read(resolved.body(), "$.status")).isEqualTo("COMPLETED");
		}
		assertThat(count("select count(*) from alerts where type = 'OUTCOME_MISMATCH' and company_id = ?::uuid", company))
			.as("reported once, however often the terminal asks").isEqualTo(1);

		// Never received, or another device's fueling: not found.
		HttpResponse<String> never = pos("POST", "/api/v1/pos/fuelings/" + UUID.randomUUID() + "/resolve", pump.deviceToken(),
				pump.session(), told);
		assertThat(never.statusCode()).isEqualTo(404);
		assertThat(code(never)).isEqualTo("FUELING_NOT_FOUND");
		Pump other = pump();
		assertThat(pos("POST", "/api/v1/pos/fuelings/" + charged + "/resolve", other.deviceToken(), other.session(), told)
			.statusCode()).isEqualTo(404);
	}

	@Test
	void anItemThatFailsOnOurSideAnswersRetryAndTheOthersAreRecorded() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		String broken = seedCompany().toString();
		Car orphan = car(broken);
		// A company with no wallet is a fault on our side, not a fact about the capture.
		Owner.update("delete from company_wallets where company_id = ?::uuid", broken);
		HttpResponse<String> answer = sync(pump, capture(pump, car(company), 10_000), capture(pump, orphan, 10_000),
				capture(pump, car(company), 10_000));
		assertThat(results(answer)).containsExactly("RECORDED", "RETRY", "RECORDED");
		assertThat(batchRows(pump).getFirst()).containsEntry("retried", 1).containsEntry("recorded", 2);
	}

	// ------------------------------------------------------------------------------ who may sync

	@Test
	void syncIsForADeviceAloneAndABatchHoldsAtMostOneHundred() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 100_000);
		Car car = car(company);
		String body = batchBody(UUID.randomUUID(), 0, List.of(capture(pump, car, 1_000)));

		// No credential, a user's token, a device that was revoked: none of them is a device.
		assertThat(post("/api/v1/pos/sync", body).statusCode()).isEqualTo(401);
		HttpResponse<String> user = pos("POST", "/api/v1/pos/sync", ops.accessToken(), null, body);
		assertThat(user.statusCode()).isEqualTo(401);
		assertThat(code(user)).isEqualTo("DEVICE_UNAUTHENTICATED");
		// The device token alone is enough: no agent has to be signed in (the queue is sent after a shift).
		assertThat(results(pos("POST", "/api/v1/pos/sync", pump.deviceToken(), null, body))).containsExactly("RECORDED");
		// An empty batch is a device saying it holds nothing.
		assertThat(results(sync(pump))).isEmpty();

		List<Capture> many = new ArrayList<>();
		for (int i = 0; i < OfflineSync.MAX_ITEMS + 1; i++) {
			many.add(capture(pump, car, 1_000));
		}
		HttpResponse<String> tooLarge = sync(pump, UUID.randomUUID(), 0, many);
		assertThat(tooLarge.statusCode()).isEqualTo(400);
		assertThat(code(tooLarge)).isEqualTo("BATCH_TOO_LARGE");
		assertThat(count("select count(*) from fuelings where company_id = ?::uuid", company)).as("none of the 101").isEqualTo(1);
		// A malformed item is the terminal's software at fault, not a capture to reject: 400 for the request.
		HttpResponse<String> malformed = pos("POST", "/api/v1/pos/sync", pump.deviceToken(), null,
				"{\"batchId\":\"" + UUID.randomUUID() + "\",\"pending\":0,\"items\":[{\"clientTxnId\":\"" + UUID.randomUUID()
						+ "\",\"volumeMl\":1000}]}");
		assertThat(malformed.statusCode()).isEqualTo(400);
		assertThat(code(malformed)).isEqualTo("VALIDATION_FAILED");

		assertThat(authed("POST", "/api/v1/ops/devices/" + pump.deviceId() + "/revoke", ops.accessToken(),
				"{\"reason\":\"Stolen\"}").statusCode()).isEqualTo(200);
		HttpResponse<String> revoked = pos("POST", "/api/v1/pos/sync", pump.deviceToken(), null,
				batchBody(UUID.randomUUID(), 0, List.of(capture(pump, car, 1_000))));
		assertThat(revoked.statusCode()).isEqualTo(401);
		assertThat(code(revoked)).isEqualTo("DEVICE_UNAUTHENTICATED");
	}

	@Test
	void eachAudienceReadsAnOfflineFuelingWithItsConflicts() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		String id = fuelingId(sync(pump, capture(pump, car(company), 10_000)), 0);

		HttpResponse<String> detail = authed("GET", "/api/v1/ops/fuelings/" + id, ops.accessToken(), null);
		assertThat(JsonPath.<String>read(detail.body(), "$.origin")).isEqualTo("OFFLINE");
		assertThat(JsonPath.<String>read(detail.body(), "$.status")).isEqualTo("COMPLETED");
		assertThat(JsonPath.<List<String>>read(detail.body(), "$.conflicts")).containsExactly("INSUFFICIENT_BALANCE");
		SignedIn accountant = signIn(seed(Role.ACCOUNTANT, new Scope(ScopeType.COMPANY, UUID.fromString(company)), false));
		assertThat(JsonPath.<List<String>>read(authed("GET", "/api/v1/company/fuelings/" + id, accountant.accessToken(), null)
			.body(), "$.conflicts")).containsExactly("INSUFFICIENT_BALANCE");
		// An online fueling has none, and the database would refuse one that claimed any.
		credit(company, 100_000);
		HttpResponse<String> online = fuel(pump, car(company), 1_000);
		assertThat(JsonPath.<List<String>>read(authed("GET", "/api/v1/ops/fuelings/" + JsonPath.<String>read(online.body(), "$.id"),
				ops.accessToken(), null).body(), "$.conflicts")).isEmpty();
	}

	// ----------------------------------------------------------------------------------- helpers

	/** Syncs one capture and checks it was recorded, completed, with exactly these conflicts and one alert. */
	private HttpResponse<String> assertRecordedWith(Pump pump, Capture capture, String severity, String... expected)
			throws Exception {
		HttpResponse<String> response = sync(pump, capture);
		assertThat(conflicts(response, 0)).as(response.body()).containsExactly(expected);
		String id = fuelingId(response, 0);
		assertThat(fuelingRow(id).get("status")).isEqualTo("COMPLETED");
		assertThat(Owner.<String>queryList("""
				select severity || ' ' || (details -> 'conflicts')::text || ' ' || company_visible from alerts
				where fueling_id = ?::uuid and type = 'SYNC_CONFLICT'
				""", id)).as("one alert for the fueling, to Operations and the company")
			.containsExactly(severity + " [\"" + String.join("\", \"", expected) + "\"] true");
		return response;
	}

	private static Map<String, Object> fuelingRow(String id) {
		return rows("select * from fuelings where id = ?::uuid", id).getFirst();
	}

	/** The device's batches as the sync monitor reads them, oldest first. */
	private static List<Map<String, Object>> batchRows(Pump pump) {
		return rows("select * from sync_batches where device_id = ?::uuid order by id", pump.deviceId());
	}

	/** The entries of one ledger transaction as "DIRECTION account-code amount". */
	private static List<String> entries(UUID transactionId) {
		return Owner.queryList("""
				select e.direction || ' ' || a.code || ' ' || e.amount_piastres
				from ledger_entries e join ledger_accounts a on a.id = e.account_id where e.transaction_id = ?
				""", transactionId);
	}

	private static List<Map<String, Object>> rows(String sql, Object... params) {
		try (var connection = Owner.connect(); var statement = connection.prepareStatement(sql)) {
			for (int i = 0; i < params.length; i++) {
				statement.setObject(i + 1, params[i]);
			}
			List<Map<String, Object>> rows = new ArrayList<>();
			try (var rs = statement.executeQuery()) {
				var meta = rs.getMetaData();
				while (rs.next()) {
					Map<String, Object> row = new java.util.LinkedHashMap<>();
					for (int column = 1; column <= meta.getColumnCount(); column++) {
						row.put(meta.getColumnName(column), rs.getObject(column));
					}
					rows.add(row);
				}
			}
			return rows;
		}
		catch (java.sql.SQLException ex) {
			throw new IllegalStateException(ex);
		}
	}

}
