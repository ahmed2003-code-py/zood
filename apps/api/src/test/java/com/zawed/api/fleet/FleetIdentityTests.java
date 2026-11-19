package com.zawed.api.fleet;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.net.http.HttpResponse;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.ThreadLocalRandom;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * Vehicle identity (V14, ADR 0024): canonical Egyptian plate keys that never merge two different
 * plates, a fleet code unique per company, a tank ceiling, and identity columns no one can rewrite.
 */
class FleetIdentityTests extends IdentityTestSupport {

	private SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	@Test
	void anEgyptianPlateIsOnePlateWhicheverOrderItIsTypedIn() throws Exception {
		UUID company = seedCompany();
		String digits = String.valueOf(1000 + ThreadLocalRandom.current().nextInt(8999));
		String letters = randomLetters();
		HttpResponse<String> first = create(company, digits + " " + letters, null, 50_000);
		assertThat(first.statusCode()).as(first.body()).isEqualTo(201);
		// Letters first, Arabic digits, no spaces: the same plate.
		HttpResponse<String> reordered = create(seedCompany(), letters.replace(" ", "") + arabic(digits), null, 50_000);
		assertThat(code(reordered)).isEqualTo("PLATE_IN_USE");
		// The plate is shown exactly as registered.
		assertThat(JsonPath.<String>read(first.body(), "$.plate")).isEqualTo(digits + " " + letters);
	}

	@Test
	void platesOfAnotherShapeAreNeverMergedByTheCanonicalForm() {
		assertThat(PlateKey.of("١٢٣٤ ط ب ع")).isEqualTo(PlateKey.of("ط ب ع 1234")).isEqualTo("1234طبع");
		assertThat(PlateKey.of("AB12CD")).isNotEqualTo(PlateKey.of("ABCD12"));
		assertThat(PlateKey.of("أ ب 12")).isEqualTo("12اب");
		assertThat(PlateKey.canonical("12AB34")).isEqualTo("12AB34");
	}

	@Test
	void aFleetCodeIsUniquePerCompanyAmongVehiclesInService() throws Exception {
		UUID company = seedCompany();
		String code = "TRK-" + UUID.randomUUID().toString().substring(0, 6);
		HttpResponse<String> first = create(company, uniquePlate(), code, 50_000);
		assertThat(JsonPath.<String>read(first.body(), "$.fleetCode")).isEqualTo(code);
		assertThat(code(create(company, uniquePlate(), code.toLowerCase(), 50_000))).isEqualTo("FLEET_CODE_IN_USE");
		assertThat(create(seedCompany(), uniquePlate(), code, 50_000).statusCode()).as("another company").isEqualTo(201);

		String vehicle = JsonPath.read(first.body(), "$.id");
		authed("POST", "/api/v1/ops/vehicles/" + vehicle + "/status", ops.accessToken(),
				"{\"status\":\"DECOMMISSIONED\",\"reason\":\"sold\"}");
		assertThat(create(company, uniquePlate(), code, 50_000).statusCode()).as("freed by decommissioning").isEqualTo(201);
	}

	@Test
	void aTankAbove1500LitresAndAnImplausibleYearAreRefused() throws Exception {
		UUID company = seedCompany();
		assertThat(create(company, uniquePlate(), null, 1_600_000).statusCode()).isEqualTo(400);
		assertThat(create(company, uniquePlate(), null, 1_500_000).statusCode()).isEqualTo(201);
		HttpResponse<String> future = authed("POST", "/api/v1/ops/vehicles", ops.accessToken(),
				"{\"companyId\":\"" + company + "\",\"vehicle\":{\"plate\":\"" + uniquePlate() + "\",\"make\":\"M\","
						+ "\"model\":\"M\",\"year\":2099,\"fuelType\":\"DIESEL\",\"tankCapacityMl\":50000,\"tier\":\"NORMAL\"}}");
		assertThat(code(future)).isEqualTo("INVALID_YEAR");
		assertThatThrownBy(() -> Owner.update("update vehicles set tank_capacity_ml = 2000000 where company_id = ?", company))
			.hasMessageContaining("vehicles_tank_capacity_ceiling");
	}

	@Test
	void identityColumnsCannotBeRewrittenEvenByTheOwner() throws Exception {
		UUID company = seedCompany();
		String vehicle = JsonPath.read(create(company, uniquePlate(), null, 50_000).body(), "$.id");
		String other = JsonPath.read(create(company, uniquePlate(), null, 50_000).body(), "$.id");
		HttpResponse<String> tag = authed("POST", "/api/v1/ops/vehicles/" + vehicle + "/tags", ops.accessToken(),
				"{\"uid\":\"" + UUID.randomUUID().toString().replace("-", "").substring(0, 14).toUpperCase() + "\"}");
		String tagId = JsonPath.read(tag.body(), "$.id");

		assertThatThrownBy(() -> Owner.update("update rfid_tags set vehicle_id = ? where id = ?", UUID.fromString(other),
				UUID.fromString(tagId))).hasMessageContaining("never change");
		assertThatThrownBy(() -> Owner.update("update rfid_tags set uid_last4 = 'FFFF' where id = ?", UUID.fromString(tagId)))
			.hasMessageContaining("never change");
		assertThatThrownBy(() -> Owner.update("update vehicles set company_id = ? where id = ?", seedCompany(),
				UUID.fromString(vehicle))).hasMessageContaining("never moves");
		// Status changes, the lifecycle, still work.
		assertThat(Owner.update("update rfid_tags set status = 'LOST' where id = ?", UUID.fromString(tagId))).isEqualTo(1);
	}

	@Test
	void racingRegistrationsOfOnePlateCreateOneVehicle() throws Exception {
		String plate = String.valueOf(1000 + ThreadLocalRandom.current().nextInt(8999)) + " " + randomLetters();
		List<UUID> companies = List.of(seedCompany(), seedCompany(), seedCompany());
		List<Integer> statuses = Concurrently.run(3, (i) -> create(companies.get(i), plate, null, 50_000).statusCode());
		assertThat(statuses).containsExactlyInAnyOrder(201, 409, 409);
		assertThat(Owner.<Long>queryOne("select count(*) from vehicles where plate = ?", plate)).isEqualTo(1L);
	}

	private HttpResponse<String> create(UUID company, String plate, String fleetCode, long tankMl) throws Exception {
		return authed("POST", "/api/v1/ops/vehicles", ops.accessToken(),
				"{\"companyId\":\"" + company + "\",\"vehicle\":{\"plate\":\"" + plate + "\",\"make\":\"Toyota\","
						+ "\"model\":\"Hilux\",\"year\":2022,\"fuelType\":\"DIESEL\",\"tankCapacityMl\":" + tankMl
						+ ",\"tier\":\"NORMAL\"" + (fleetCode == null ? "" : ",\"fleetCode\":\"" + fleetCode + "\"") + "}}");
	}

	private static String uniquePlate() {
		return "TST " + UUID.randomUUID().toString().substring(0, 8).toUpperCase();
	}

	/** Three distinct letters from those Egyptian plates use, space separated. */
	private static String randomLetters() {
		String alphabet = "أبجدرسصطعفقكلمنهوي";
		StringBuilder out = new StringBuilder();
		ThreadLocalRandom random = ThreadLocalRandom.current();
		for (int i = 0; i < 3; i++) {
			out.append(i == 0 ? "" : " ").append(alphabet.charAt(random.nextInt(alphabet.length())));
		}
		return out.toString();
	}

	private static String arabic(String digits) {
		StringBuilder out = new StringBuilder();
		digits.chars().forEach((c) -> out.append((char) ('٠' + (c - '0'))));
		return out.toString();
	}

}
