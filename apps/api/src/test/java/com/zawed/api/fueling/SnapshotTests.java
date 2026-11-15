package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.HexFormat;
import java.util.List;
import java.util.Map;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;

/**
 * The POS snapshot and the sync monitor (B5 contract §1, §2, §5): what a device may know to work
 * offline, and what it must never be given.
 */
class SnapshotTests extends PosTestSupport {

	@Test
	void theSnapshotLetsTheDeviceRecogniseATagWithoutEverHoldingItsUid() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		credit(company, 123_456);
		Car car = car(company);
		assertThat(authed("PUT", "/api/v1/ops/vehicles/" + car.vehicleId() + "/limits", ops.accessToken(),
				"{\"kind\":\"VOLUME\",\"period\":\"DAY\",\"maxValue\":50000}").statusCode()).isEqualTo(200);
		assertThat(fuel(pump, car, 10_000).statusCode()).isEqualTo(201);

		HttpResponse<String> response = snapshot(pump.deviceToken());
		assertThat(response.statusCode()).as(response.body()).isEqualTo(200);
		assertThat(response.headers().firstValue("Cache-Control").orElse("")).contains("no-store");
		String body = response.body();

		// The tag, by digest only.
		assertThat(body).doesNotContain(car.tagUid()).doesNotContain(car.tagUid().toLowerCase());
		String digest = digest(JsonPath.read(body, "$.tagKey"), car.tagUid());
		List<String> vehicles = JsonPath.read(body, "$.tags[?(@.digest == '" + digest + "')].vehicleId");
		assertThat(vehicles).containsExactly(car.vehicleId());
		assertThat(JsonPath.<List<Object>>read(body, "$.tags[?(@.digest == '" + digest + "')].cooldownUntil").get(0))
			.as("the cooldown the fueling started").isNotNull();

		// The wallet as a word and a capped allowance, never the balance.
		Map<String, Object> wallet = JsonPath.<List<Map<String, Object>>>read(body, "$.companies[?(@.id == '" + company + "')]")
			.get(0);
		assertThat(wallet.get("walletStatus")).isEqualTo("AVAILABLE");
		long balanceNow = 123_456 - 15_000;
		assertThat(((Number) wallet.get("offlineAllowancePiastres")).longValue()).isEqualTo(balanceNow);
		assertThat(body).doesNotContain("balance");

		// The day limit with what is already used.
		Map<String, Object> limit = JsonPath.<List<Map<String, Object>>>read(body,
				"$.limits[?(@.vehicleId == '" + car.vehicleId() + "')]").get(0);
		assertThat(((Number) limit.get("used")).longValue()).isEqualTo(10_000);
		assertThat(((Number) limit.get("maxValue")).longValue()).isEqualTo(50_000);

		// Prices, the station, the switches, and nothing about agents.
		assertThat(JsonPath.<List<Integer>>read(body, "$.prices[?(@.fuelType == 'GAS_92')].pricePiastresPerLitre"))
			.contains((int) PRICE);
		assertThat(JsonPath.<String>read(body, "$.station.id")).isEqualTo(pump.stationId());
		assertThat(JsonPath.<Boolean>read(body, "$.vipEnabled")).isFalse();
		assertThat(body).doesNotContain("pin").doesNotContain(pump.workerId());
	}

	@Test
	void theAllowanceIsCappedSoTheBalanceStaysPrivate() throws Exception {
		Pump pump = pump();
		String company = seedCompany().toString();
		credit(company, 9_000_000);
		car(company);
		String body = snapshot(pump.deviceToken()).body();
		Map<String, Object> wallet = JsonPath.<List<Map<String, Object>>>read(body, "$.companies[?(@.id == '" + company + "')]")
			.get(0);
		assertThat(((Number) wallet.get("offlineAllowancePiastres")).longValue()).isEqualTo(500_000);
		assertThat(body).doesNotContain("9000000");
	}

	@Test
	void eachDeviceGetsItsOwnTagKey() throws Exception {
		Pump first = pump();
		Pump second = pump();
		Car car = car(seedCompany().toString());
		String a = JsonPath.read(snapshot(first.deviceToken()).body(), "$.tagKey");
		String b = JsonPath.read(snapshot(second.deviceToken()).body(), "$.tagKey");
		assertThat(a).isNotEqualTo(b);
		assertThat(digest(a, car.tagUid())).isNotEqualTo(digest(b, car.tagUid()));
	}

	@Test
	void onlyADeviceGetsASnapshot() throws Exception {
		HttpResponse<String> user = authed("GET", "/api/v1/pos/snapshot", ops.accessToken(), null);
		assertThat(user.statusCode()).isEqualTo(401);
		assertThat(code(user)).isEqualTo("DEVICE_UNAUTHENTICATED");
		assertThat(get("/api/v1/pos/snapshot").statusCode()).isEqualTo(401);
	}

	@Test
	void anOutdatedTerminalIsToldToUpdate() throws Exception {
		Pump pump = pump();
		HttpResponse<String> old = send(request("/api/v1/pos/snapshot").header("Authorization", "Bearer " + pump.deviceToken())
			.header("X-Zawed-Contract", "0")
			.GET());
		assertThat(old.statusCode()).isEqualTo(426);
		assertThat(code(old)).isEqualTo("CONTRACT_UPGRADE_REQUIRED");
		assertThat(JsonPath.<Integer>read(old.body(), "$.params.minimum")).isEqualTo(1);
		HttpResponse<String> current = send(request("/api/v1/pos/snapshot")
			.header("Authorization", "Bearer " + pump.deviceToken())
			.header("X-Zawed-Contract", "1")
			.GET());
		assertThat(current.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<Integer>read(get("/api/v1/meta").body(), "$.posContract")).isEqualTo(1);
	}

	@Test
	void theSyncMonitorShowsEachAudienceItsDevices() throws Exception {
		Pump pump = pump();
		snapshot(pump.deviceToken());
		HttpResponse<String> all = authed("GET", "/api/v1/ops/sync/devices?operatorId=" + pump.operatorId(), ops.accessToken(),
				null);
		assertThat(all.statusCode()).as(all.body()).isEqualTo(200);
		assertThat(JsonPath.<List<String>>read(all.body(), "$.items[*].deviceId")).containsExactly(pump.deviceId());
		assertThat(JsonPath.<Boolean>read(all.body(), "$.items[0].stale")).isFalse();
		assertThat(JsonPath.<String>read(all.body(), "$.items[0].lastSyncAt")).isNotNull();

		SignedIn station = signIn(seed(Role.STATION_MANAGER,
				new Scope(ScopeType.OPERATOR, java.util.UUID.fromString(pump.operatorId())), false));
		List<String> mine = JsonPath.read(authed("GET", "/api/v1/station/sync/devices", station.accessToken(), null).body(),
				"$.items[*].deviceId");
		assertThat(mine).containsExactly(pump.deviceId());
		SignedIn other = signIn(seed(Role.STATION_MANAGER));
		List<String> none = JsonPath.read(authed("GET", "/api/v1/station/sync/devices", other.accessToken(), null).body(),
				"$.items[*].deviceId");
		assertThat(none).isEmpty();
	}

	// ----------------------------------------------------------------------------------- helpers

	private HttpResponse<String> snapshot(String deviceToken) throws Exception {
		return send(request("/api/v1/pos/snapshot").header("Authorization", "Bearer " + deviceToken).GET());
	}

	/** What the device computes: HMAC-SHA256(tagKey, normalised UID), lower-case hex. */
	static String digest(String tagKeyBase64, String uid) throws Exception {
		Mac mac = Mac.getInstance("HmacSHA256");
		mac.init(new SecretKeySpec(Base64.getDecoder().decode(tagKeyBase64), "HmacSHA256"));
		return HexFormat.of().formatHex(mac.doFinal(uid.toUpperCase().getBytes(StandardCharsets.US_ASCII)));
	}

}
