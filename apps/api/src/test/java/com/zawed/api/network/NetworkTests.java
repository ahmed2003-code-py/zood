package com.zawed.api.network;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Owner;

/** Operators, stations, POS devices and agents: one-time secrets and the station portal's view. */
class NetworkTests extends IdentityTestSupport {

	private SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	@Test
	void aDeviceActivationCodeIsShownOnceAndStoredOnlyAsAKeyedHash() throws Exception {
		String station = station(operator());
		HttpResponse<String> registered = authed("POST", "/api/v1/ops/devices", ops.accessToken(),
				"{\"stationId\":\"" + station + "\",\"label\":\"Pump 1\"}");
		assertThat(registered.statusCode()).as(registered.body()).isEqualTo(201);
		assertThat(registered.headers().firstValue("Cache-Control").orElse("")).contains("no-store");

		String code = JsonPath.read(registered.body(), "$.activationCode");
		String device = JsonPath.read(registered.body(), "$.device.id");
		assertThat(code).matches("[A-Z2-9]{12}");
		assertThat(JsonPath.<String>read(registered.body(), "$.device.status")).isEqualTo("PENDING_ACTIVATION");
		String stored = Owner.queryOne("select activation_code_hash from pos_devices where id = ?", UUID.fromString(device));
		assertThat(stored).isNotEqualTo(code).hasSize(64);

		// Reading the device later never returns the code.
		HttpResponse<String> read = authed("GET", "/api/v1/ops/devices/" + device, ops.accessToken(), null);
		assertThat(read.body()).doesNotContain(code).doesNotContain("activationCode");

		// A new code replaces the old one; after revocation there is no code at all.
		String again = JsonPath.read(authed("POST", "/api/v1/ops/devices/" + device + "/activation-code",
				ops.accessToken(), null).body(), "$.activationCode");
		assertThat(again).isNotEqualTo(code);
		assertThat(authed("POST", "/api/v1/ops/devices/" + device + "/revoke", ops.accessToken(),
				"{\"reason\":\"Stolen\"}").statusCode()).isEqualTo(200);
		assertThat(Owner.<Object>queryOne("select activation_code_hash from pos_devices where id = ?",
				UUID.fromString(device))).isNull();
		assertThat(code(authed("POST", "/api/v1/ops/devices/" + device + "/activation-code", ops.accessToken(), null)))
			.isEqualTo("DEVICE_NOT_PENDING");
	}

	@Test
	void anAgentPinIsGeneratedShownOnceAndHashedWithArgon2() throws Exception {
		String station = station(operator());
		HttpResponse<String> created = authed("POST", "/api/v1/ops/agents", ops.accessToken(),
				"{\"stationId\":\"" + station + "\",\"fullName\":\"Mahmoud Adel\",\"phone\":\"01000000000\"}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		assertThat(created.headers().firstValue("Cache-Control").orElse("")).contains("no-store");

		String pin = JsonPath.read(created.body(), "$.pin");
		String agent = JsonPath.read(created.body(), "$.agent.id");
		assertThat(pin).matches("\\d{6}");
		assertThat(Agents.isTrivial(pin)).isFalse();
		assertThat(JsonPath.<String>read(created.body(), "$.agent.workerId")).matches("W\\d{7}");
		assertThat(JsonPath.<Boolean>read(created.body(), "$.agent.mustChangePin")).isTrue();
		assertThat(Owner.<String>queryOne("select pin_hash from agents where id = ?", UUID.fromString(agent)))
			.startsWith("$argon2id$");

		HttpResponse<String> reset = authed("POST", "/api/v1/ops/agents/" + agent + "/reset-pin", ops.accessToken(),
				null);
		assertThat(reset.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(reset.body(), "$.pin")).matches("\\d{6}");
		assertThat(authed("GET", "/api/v1/ops/agents/" + agent, ops.accessToken(), null).body()).doesNotContain("pin\"");
	}

	@Test
	void trivialPinsAreRecognised() {
		assertThat(Agents.isTrivial("000000")).isTrue();
		assertThat(Agents.isTrivial("123456")).isTrue();
		assertThat(Agents.isTrivial("987654")).isTrue();
		assertThat(Agents.isTrivial("120456")).isFalse();
	}

	@Test
	void aStationManagerSeesOnlyTheirOperator() throws Exception {
		String mine = operator();
		String theirs = operator();
		String myStation = station(mine);
		String theirStation = station(theirs);

		SignedIn manager = signIn(seed(Role.STATION_MANAGER, Scope.operator(UUID.fromString(mine)), false));
		List<String> stations = JsonPath.read(authed("GET", "/api/v1/station/stations", manager.accessToken(), null)
			.body(), "$.items[*].id");
		assertThat(stations).containsExactly(myStation);
		assertThat(authed("GET", "/api/v1/station/stations/" + theirStation, manager.accessToken(), null).statusCode())
			.isEqualTo(404);
		assertThat(JsonPath.<String>read(authed("GET", "/api/v1/station/operator", manager.accessToken(), null).body(),
				"$.id")).isEqualTo(mine);
	}

	@Test
	void aClosedStationIsFinal() throws Exception {
		String station = station(operator());
		assertThat(authed("POST", "/api/v1/ops/stations/" + station + "/status", ops.accessToken(),
				"{\"status\":\"CLOSED\",\"reason\":\"Demolished\"}").statusCode()).isEqualTo(200);
		assertThat(code(authed("POST", "/api/v1/ops/stations/" + station + "/status", ops.accessToken(),
				"{\"status\":\"ACTIVE\",\"reason\":\"Oops\"}"))).isEqualTo("STATION_CLOSED");
		assertThat(code(authed("POST", "/api/v1/ops/devices", ops.accessToken(), "{\"stationId\":\"" + station + "\"}")))
			.isEqualTo("STATION_CLOSED");
	}

	@Test
	void anUnknownTimeZoneIsRefused() throws Exception {
		HttpResponse<String> response = authed("POST", "/api/v1/ops/stations", ops.accessToken(),
				"{\"operatorId\":\"" + operator() + "\",\"station\":{\"nameAr\":\"م\",\"nameEn\":\"S\",\"timeZone\":\"Mars/Base\","
						+ "\"connectivity\":\"LOW\",\"fuelTypes\":[\"DIESEL\"]}}");
		assertThat(response.statusCode()).isEqualTo(400);
		assertThat(code(response)).isEqualTo("INVALID_TIME_ZONE");
	}

	@Test
	void anAgentPhoneIsAnEgyptianMobileStoredAs01XXXXXXXXX() throws Exception {
		String station = station(operator());
		HttpResponse<String> created = authed("POST", "/api/v1/ops/agents", ops.accessToken(),
				"{\"stationId\":\"" + station + "\",\"fullName\":\"Phone Agent\",\"phone\":\"+20 112 345 6789\"}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(created.body(), "$.agent.phone")).isEqualTo("01123456789");

		for (String phone : new String[] { "0223456789", "12345", "01301234567", "agent" }) {
			HttpResponse<String> refused = authed("POST", "/api/v1/ops/agents", ops.accessToken(),
					"{\"stationId\":\"" + station + "\",\"fullName\":\"Phone Agent\",\"phone\":\"" + phone + "\"}");
			assertThat(refused.statusCode()).as(phone + " " + refused.body()).isEqualTo(400);
			assertThat(JsonPath.<List<String>>read(refused.body(), "$.fields[*].field")).containsExactly("phone");
		}
		HttpResponse<String> blank = authed("POST", "/api/v1/ops/agents", ops.accessToken(),
				"{\"stationId\":\"" + station + "\",\"fullName\":\"No Phone\",\"phone\":\" \"}");
		assertThat(blank.statusCode()).as(blank.body()).isEqualTo(201);
		assertThat(JsonPath.<Object>read(blank.body(), "$.agent.phone")).isNull();
	}

	/**
	 * QA round 1: a device whose activation code had expired showed "awaiting activation", and a locked
	 * agent showed "active". Ops and the station manager now read both states from the API.
	 */
	@Test
	void opsAndTheStationManagerSeeAnExpiredActivationCodeAndALockedAgent() throws Exception {
		String operator = operator();
		String station = station(operator);
		String device = JsonPath.read(authed("POST", "/api/v1/ops/devices", ops.accessToken(),
				"{\"stationId\":\"" + station + "\"}").body(), "$.device.id");
		HttpResponse<String> agentCreated = authed("POST", "/api/v1/ops/agents", ops.accessToken(),
				"{\"stationId\":\"" + station + "\",\"fullName\":\"Locked Agent\"}");
		String agent = JsonPath.read(agentCreated.body(), "$.agent.id");
		assertThat(JsonPath.<Boolean>read(agentCreated.body(), "$.agent.locked")).isFalse();
		assertThat(JsonPath.<Boolean>read(agentCreated.body(), "$.agent.mustChangePin")).isTrue();

		HttpResponse<String> fresh = authed("GET", "/api/v1/ops/devices/" + device, ops.accessToken(), null);
		assertThat(JsonPath.<Boolean>read(fresh.body(), "$.activationExpired")).isFalse();

		// Fifteen minutes later the code no longer works, and an agent typed a wrong PIN too often.
		Owner.update("update pos_devices set activation_expires_at = now() - interval '1 minute' where id = ?",
				UUID.fromString(device));
		Owner.update("update agents set locked_until = now() + interval '10 minutes', failed_pin_count = 5 where id = ?",
				UUID.fromString(agent));

		HttpResponse<String> expired = authed("GET", "/api/v1/ops/devices/" + device, ops.accessToken(), null);
		assertThat(JsonPath.<String>read(expired.body(), "$.status")).isEqualTo("PENDING_ACTIVATION");
		assertThat(JsonPath.<Boolean>read(expired.body(), "$.activationExpired")).isTrue();
		HttpResponse<String> locked = authed("GET", "/api/v1/ops/agents/" + agent, ops.accessToken(), null);
		assertThat(JsonPath.<Boolean>read(locked.body(), "$.locked")).isTrue();
		assertThat(JsonPath.<String>read(locked.body(), "$.lockedUntil")).isNotNull();

		SignedIn manager = signIn(seed(Role.STATION_MANAGER, Scope.operator(UUID.fromString(operator)), false));
		HttpResponse<String> devices = authed("GET", "/api/v1/station/devices", manager.accessToken(), null);
		assertThat(JsonPath.<List<Boolean>>read(devices.body(), "$.items[?(@.id == '" + device + "')].activationExpired"))
			.containsExactly(true);
		HttpResponse<String> agents = authed("GET", "/api/v1/station/agents", manager.accessToken(), null);
		assertThat(JsonPath.<List<Boolean>>read(agents.body(), "$.items[?(@.id == '" + agent + "')].locked"))
			.containsExactly(true);
		assertThat(JsonPath.<List<Boolean>>read(agents.body(), "$.items[?(@.id == '" + agent + "')].mustChangePin"))
			.containsExactly(true);

		// Ops reissues a code, and the device is no longer expired. A lock in the past is no lock.
		authed("POST", "/api/v1/ops/devices/" + device + "/activation-code", ops.accessToken(), null);
		assertThat(JsonPath.<Boolean>read(authed("GET", "/api/v1/ops/devices/" + device, ops.accessToken(), null).body(),
				"$.activationExpired")).isFalse();
		Owner.update("update agents set locked_until = now() - interval '1 minute' where id = ?", UUID.fromString(agent));
		assertThat(JsonPath.<Boolean>read(authed("GET", "/api/v1/ops/agents/" + agent, ops.accessToken(), null).body(),
				"$.locked")).isFalse();
	}

	private String operator() throws Exception {
		HttpResponse<String> created = authed("POST", "/api/v1/ops/operators", ops.accessToken(),
				"{\"legalName\":\"Op " + UUID.randomUUID() + "\",\"nameAr\":\"مشغل\",\"nameEn\":\"Operator\"}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		return JsonPath.read(created.body(), "$.id");
	}

	private String station(String operator) throws Exception {
		HttpResponse<String> created = authed("POST", "/api/v1/ops/stations", ops.accessToken(),
				"{\"operatorId\":\"" + operator + "\",\"station\":{\"nameAr\":\"محطة\",\"nameEn\":\"Station\","
						+ "\"latitude\":30.0444,\"longitude\":31.2357,\"connectivity\":\"NORMAL\","
						+ "\"fuelTypes\":[\"GAS_92\",\"DIESEL\"]}}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(created.body(), "$.timeZone")).isEqualTo("Africa/Cairo");
		return JsonPath.read(created.body(), "$.id");
	}

}
