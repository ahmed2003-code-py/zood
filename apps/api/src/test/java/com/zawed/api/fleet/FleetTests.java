package com.zawed.api.fleet;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Owner;

/** Vehicles, RFID tags and limits: the rules of PRD §4.3–4.5 and the company's read-only view. */
class FleetTests extends IdentityTestSupport {

	private SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	@Test
	void aPlateIsUniqueWhateverTheSpelling() throws Exception {
		UUID company = seedCompany();
		String plate = "ق ص م " + (1000 + (int) (Math.random() * 8000));
		assertThat(createVehicle(company, plate, "NORMAL", null).statusCode()).isEqualTo(201);

		// Same plate without spaces and with Eastern Arabic digits.
		String eastern = toEasternDigits(plate.replace(" ", ""));
		HttpResponse<String> duplicate = createVehicle(company, eastern, "NORMAL", null);
		assertThat(duplicate.statusCode()).isEqualTo(409);
		assertThat(code(duplicate)).isEqualTo("PLATE_IN_USE");

		// Search folds the same way; a query with nothing a plate could contain matches nothing.
		String digits = plate.substring(plate.length() - 4);
		String query = java.net.URLEncoder.encode(toEasternDigits(digits), java.nio.charset.StandardCharsets.UTF_8);
		List<String> found = JsonPath.read(authed("GET", "/api/v1/ops/vehicles?companyId=" + company + "&q=" + query,
				ops.accessToken(), null).body(), "$.items[*].plate");
		assertThat(found).containsExactly(plate);
		List<Object> none = JsonPath.read(authed("GET", "/api/v1/ops/vehicles?q=--", ops.accessToken(), null).body(),
				"$.items");
		assertThat(none).isEmpty();
	}

	@Test
	void aVipCeilingIsForVipVehiclesOnlyAndHiddenFromTheCompany() throws Exception {
		UUID company = seedCompany();
		assertThat(code(createVehicle(company, uniquePlate(), "NORMAL", 100_000L))).isEqualTo("VIP_CEILING_ONLY_FOR_VIP");

		HttpResponse<String> vip = createVehicle(company, uniquePlate(), "VIP", 300_000L);
		assertThat(vip.statusCode()).isEqualTo(201);
		String id = JsonPath.read(vip.body(), "$.id");
		assertThat(JsonPath.<Integer>read(vip.body(), "$.vipMonthlyCeilingMl")).isEqualTo(300_000);

		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		HttpResponse<String> seen = authed("GET", "/api/v1/company/vehicles/" + id, manager.accessToken(), null);
		assertThat(seen.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<Object>read(seen.body(), "$.vehicle.vipMonthlyCeilingMl")).isNull();
	}

	@Test
	void aCompanySeesOnlyItsOwnVehicles() throws Exception {
		UUID mine = seedCompany();
		UUID theirs = seedCompany();
		String myVehicle = JsonPath.read(createVehicle(mine, uniquePlate(), "NORMAL", null).body(), "$.id");
		String theirVehicle = JsonPath.read(createVehicle(theirs, uniquePlate(), "NORMAL", null).body(), "$.id");

		SignedIn accountant = signIn(seed(Role.ACCOUNTANT, Scope.company(mine), false));
		HttpResponse<String> list = authed("GET", "/api/v1/company/vehicles?limit=100", accountant.accessToken(), null);
		List<String> ids = JsonPath.read(list.body(), "$.items[*].id");
		assertThat(ids).contains(myVehicle).doesNotContain(theirVehicle);

		// Guessing another company's id gives the same answer as an id that does not exist.
		HttpResponse<String> guessed = authed("GET", "/api/v1/company/vehicles/" + theirVehicle,
				accountant.accessToken(), null);
		assertThat(guessed.statusCode()).isEqualTo(404);
	}

	@Test
	void aTagIsStoredEncryptedAndShownByItsLastFourOnly() throws Exception {
		String vehicle = vehicle();
		String uid = randomUid();
		HttpResponse<String> issued = issueTag(vehicle, uid.toLowerCase().replaceAll("(..)(?!$)", "$1:"));
		assertThat(issued.statusCode()).as(issued.body()).isEqualTo(201);
		assertThat(issued.body()).doesNotContain(uid).doesNotContain(uid.toLowerCase());
		assertThat(JsonPath.<String>read(issued.body(), "$.uidLast4")).isEqualTo(uid.substring(uid.length() - 4));

		byte[] stored = Owner.queryOne("select uid_encrypted from rfid_tags where id = ?",
				UUID.fromString(JsonPath.read(issued.body(), "$.id")));
		assertThat(new String(stored, StandardCharsets.ISO_8859_1)).doesNotContain(uid);
		String lookup = Owner.queryOne("select uid_lookup from rfid_tags where id = ?",
				UUID.fromString(JsonPath.read(issued.body(), "$.id")));
		assertThat(lookup).isNotEqualToIgnoringCase(uid).hasSize(64);
	}

	@Test
	void oneActiveTagPerVehicleAndALostTagIsNeverBoundAgain() throws Exception {
		String vehicle = vehicle();
		String first = randomUid();
		String firstTag = JsonPath.read(issueTag(vehicle, first).body(), "$.id");

		HttpResponse<String> second = issueTag(vehicle, randomUid());
		assertThat(second.statusCode()).isEqualTo(409);
		assertThat(code(second)).isEqualTo("VEHICLE_HAS_ACTIVE_TAG");

		assertThat(tagStatus(firstTag, "LOST").statusCode()).isEqualTo(200);
		// Final: a lost tag cannot come back.
		assertThat(code(tagStatus(firstTag, "ACTIVE"))).isEqualTo("INVALID_TAG_TRANSITION");

		HttpResponse<String> rebound = issueTag(vehicle(), first);
		assertThat(rebound.statusCode()).isEqualTo(409);
		assertThat(code(rebound)).isEqualTo("TAG_ALREADY_BOUND");

		assertThat(issueTag(vehicle, randomUid()).statusCode()).isEqualTo(201);
	}

	@Test
	void aReplacementTagInheritsTheCooldown() throws Exception {
		String vehicle = vehicle();
		String oldTag = JsonPath.read(issueTag(vehicle, randomUid()).body(), "$.id");
		Owner.update("update rfid_tags set cooldown_until = now() + interval '2 hours' where id = ?",
				UUID.fromString(oldTag));
		assertThat(tagStatus(oldTag, "REPLACEMENT_PENDING").statusCode()).isEqualTo(200);

		HttpResponse<String> replacement = issueTag(vehicle, randomUid());
		assertThat(replacement.statusCode()).isEqualTo(201);
		assertThat(JsonPath.<String>read(replacement.body(), "$.cooldownUntil")).isNotNull();
		assertThat(Owner.<String>queryOne("select status from rfid_tags where id = ?", UUID.fromString(oldTag)))
			.isEqualTo("DEACTIVATED");
	}

	@Test
	void aLimitReplacesThePreviousOneForTheSameKindAndPeriod() throws Exception {
		String vehicle = vehicle();
		setLimit(vehicle, "VOLUME", "DAY", 60_000);
		setLimit(vehicle, "AMOUNT", "MONTH", 500_000);
		HttpResponse<String> replaced = setLimit(vehicle, "VOLUME", "DAY", 40_000);

		List<Map<String, Object>> limits = JsonPath.read(replaced.body(), "$");
		assertThat(limits).hasSize(2);
		assertThat(limits).anySatisfy((limit) -> {
			assertThat(limit.get("kind")).isEqualTo("VOLUME");
			assertThat(limit.get("maxValue")).isEqualTo(40_000);
		});
		Long retired = Owner.queryOne("select count(*) from limit_rules where vehicle_id = ? and not active",
				UUID.fromString(vehicle));
		assertThat(retired).isEqualTo(1L);

		String limitId = (String) limits.getFirst().get("id");
		assertThat(authed("DELETE", "/api/v1/ops/vehicles/" + vehicle + "/limits/" + limitId, ops.accessToken(), null)
			.statusCode()).isEqualTo(204);
		HttpResponse<String> detail = authed("GET", "/api/v1/ops/vehicles/" + vehicle, ops.accessToken(), null);
		assertThat(JsonPath.<List<Object>>read(detail.body(), "$.limits")).hasSize(1);
	}

	@Test
	void decommissioningDeactivatesTagsAndFreesThePlate() throws Exception {
		UUID company = seedCompany();
		String plate = uniquePlate();
		String vehicle = JsonPath.read(createVehicle(company, plate, "NORMAL", null).body(), "$.id");
		issueTag(vehicle, randomUid());

		HttpResponse<String> done = authed("POST", "/api/v1/ops/vehicles/" + vehicle + "/status", ops.accessToken(),
				"{\"status\":\"DECOMMISSIONED\",\"reason\":\"Sold\"}");
		assertThat(done.statusCode()).isEqualTo(200);
		assertThat(Owner.<Long>queryOne("select count(*) from rfid_tags where vehicle_id = ? and status = 'ACTIVE'",
				UUID.fromString(vehicle))).isZero();
		assertThat(code(authed("POST", "/api/v1/ops/vehicles/" + vehicle + "/status", ops.accessToken(),
				"{\"status\":\"ACTIVE\",\"reason\":\"Oops\"}"))).isEqualTo("VEHICLE_DECOMMISSIONED");
		assertThat(createVehicle(company, plate, "NORMAL", null).statusCode()).isEqualTo(201);
	}

	@Test
	void aDepartmentMustBelongToTheVehiclesCompany() throws Exception {
		UUID company = seedCompany();
		UUID other = seedCompany();
		String department = JsonPath.read(authed("POST", "/api/v1/ops/companies/" + other + "/departments",
				ops.accessToken(), "{\"name\":\"Logistics\"}").body(), "$.id");

		HttpResponse<String> response = authed("POST", "/api/v1/ops/vehicles", ops.accessToken(),
				"{\"companyId\":\"" + company + "\",\"vehicle\":{\"departmentId\":\"" + department + "\",\"plate\":\""
						+ uniquePlate() + "\",\"make\":\"Toyota\",\"model\":\"Hilux\",\"fuelType\":\"DIESEL\","
						+ "\"tankCapacityMl\":80000,\"tier\":\"NORMAL\"}}");
		assertThat(response.statusCode()).isEqualTo(400);
		assertThat(code(response)).isEqualTo("DEPARTMENT_NOT_FOUND");
	}

	@Test
	void everyChangeIsAudited() throws Exception {
		UUID company = seedCompany();
		String vehicle = JsonPath.read(createVehicle(company, uniquePlate(), "NORMAL", null).body(), "$.id");
		issueTag(vehicle, randomUid());
		setLimit(vehicle, "VOLUME", "WEEK", 200_000);
		List<String> actions = Owner.queryList("select action from audit_log where scope_id = ? order by occurred_at",
				company);
		assertThat(actions).containsSubsequence("VEHICLE_CREATED", "TAG_ISSUED", "LIMIT_SET");
	}

	// ------------------------------------------------------------------------------- concurrency

	@Test
	void theSameTagRacingOntoTwoVehiclesIsBoundOnce() throws Exception {
		String uid = randomUid();
		List<String> vehicles = List.of(vehicle(), vehicle());
		List<Integer> statuses = race(vehicles.size(), (i) -> issueTag(vehicles.get(i), uid).statusCode());
		assertThat(statuses).containsExactlyInAnyOrder(201, 409);
	}

	@Test
	void twoTagsRacingOntoOneVehicleLeaveOneActive() throws Exception {
		String vehicle = vehicle();
		List<Integer> statuses = race(2, (i) -> issueTag(vehicle, randomUid()).statusCode());
		assertThat(statuses).containsExactlyInAnyOrder(201, 409);
		assertThat(Owner.<Long>queryOne("select count(*) from rfid_tags where vehicle_id = ? and status = 'ACTIVE'",
				UUID.fromString(vehicle))).isEqualTo(1L);
	}

	@Test
	void racingLimitChangesLeaveOneActiveRule() throws Exception {
		String vehicle = vehicle();
		List<Integer> statuses = race(4, (i) -> authed("PUT", "/api/v1/ops/vehicles/" + vehicle + "/limits",
				ops.accessToken(), "{\"kind\":\"VOLUME\",\"period\":\"DAY\",\"maxValue\":" + (10_000 + i) + "}")
			.statusCode());
		assertThat(statuses).containsOnly(200);
		assertThat(Owner.<Long>queryOne(
				"select count(*) from limit_rules where vehicle_id = ? and kind = 'VOLUME' and period = 'DAY' and active",
				UUID.fromString(vehicle))).isEqualTo(1L);
	}

	/** Runs {@code n} calls at the same moment and returns their results. */
	private static <T> List<T> race(int n, Call<T> call) throws Exception {
		var start = new java.util.concurrent.CountDownLatch(1);
		try (var pool = java.util.concurrent.Executors.newFixedThreadPool(n)) {
			List<java.util.concurrent.Future<T>> futures = new java.util.ArrayList<>();
			for (int i = 0; i < n; i++) {
				int index = i;
				futures.add(pool.submit(() -> {
					start.await();
					return call.run(index);
				}));
			}
			start.countDown();
			List<T> results = new java.util.ArrayList<>();
			for (var future : futures) {
				results.add(future.get(30, java.util.concurrent.TimeUnit.SECONDS));
			}
			return results;
		}
	}

	@FunctionalInterface
	private interface Call<T> {

		T run(int index) throws Exception;

	}

	// ----------------------------------------------------------------------------------- helpers

	private String vehicle() throws Exception {
		HttpResponse<String> created = createVehicle(seedCompany(), uniquePlate(), "NORMAL", null);
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		return JsonPath.read(created.body(), "$.id");
	}

	private HttpResponse<String> createVehicle(UUID company, String plate, String tier, Long ceiling) throws Exception {
		return authed("POST", "/api/v1/ops/vehicles", ops.accessToken(),
				"{\"companyId\":\"" + company + "\",\"vehicle\":{\"plate\":\"" + plate + "\",\"make\":\"Toyota\","
						+ "\"model\":\"Corolla\",\"year\":2022,\"fuelType\":\"GAS_92\",\"tankCapacityMl\":50000,"
						+ "\"tier\":\"" + tier + "\"" + (ceiling == null ? "" : ",\"vipMonthlyCeilingMl\":" + ceiling)
						+ "}}");
	}

	private HttpResponse<String> issueTag(String vehicle, String uid) throws Exception {
		return authed("POST", "/api/v1/ops/vehicles/" + vehicle + "/tags", ops.accessToken(), "{\"uid\":\"" + uid + "\"}");
	}

	private HttpResponse<String> tagStatus(String tag, String status) throws Exception {
		return authed("POST", "/api/v1/ops/tags/" + tag + "/status", ops.accessToken(),
				"{\"status\":\"" + status + "\",\"reason\":\"test\"}");
	}

	private HttpResponse<String> setLimit(String vehicle, String kind, String period, long max) throws Exception {
		HttpResponse<String> response = authed("PUT", "/api/v1/ops/vehicles/" + vehicle + "/limits", ops.accessToken(),
				"{\"kind\":\"" + kind + "\",\"period\":\"" + period + "\",\"maxValue\":" + max + "}");
		assertThat(response.statusCode()).as(response.body()).isEqualTo(200);
		return response;
	}

	private static String uniquePlate() {
		return "TST " + UUID.randomUUID().toString().substring(0, 8).toUpperCase();
	}

	private static String randomUid() {
		return UUID.randomUUID().toString().replace("-", "").substring(0, 14).toUpperCase();
	}

	private static String toEasternDigits(String value) {
		StringBuilder out = new StringBuilder();
		value.chars().forEach((c) -> out.append(c >= '0' && c <= '9' ? (char) ('٠' + (c - '0')) : (char) c));
		return out.toString();
	}

}
