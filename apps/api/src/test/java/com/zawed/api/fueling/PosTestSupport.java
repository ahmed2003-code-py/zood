package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.Signature;
import java.security.spec.ECGenParameterSpec;
import java.time.Instant;
import java.time.LocalDate;
import java.util.Base64;
import java.util.HexFormat;
import java.util.UUID;
import java.util.concurrent.ThreadLocalRandom;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;

/**
 * Sets up a working pump through the real API, the way Operations and the installer would: an
 * operator and station, a device activated with its own key pair, an agent who has changed the PIN,
 * a company with a vehicle, a tag, a price and money in the wallet.
 */
abstract class PosTestSupport extends IdentityTestSupport {

	static final long PRICE = 1_500; // piastres per litre

	SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	/** A device with its key, its token, and an agent signed in on it. */
	record Pump(String deviceId, String deviceCode, String stationId, String operatorId, KeyPair keys,
			String deviceToken, String workerId, String pin, String session) {

		Pump withSession(String newSession) {
			return new Pump(deviceId, deviceCode, stationId, operatorId, keys, deviceToken, workerId, pin, newSession);
		}

	}

	record Car(String companyId, String vehicleId, String tagUid) {
	}

	// ------------------------------------------------------------------------------------ network

	String operator() throws Exception {
		HttpResponse<String> created = authed("POST", "/api/v1/ops/operators", ops.accessToken(),
				"{\"legalName\":\"Op " + UUID.randomUUID() + "\",\"nameAr\":\"مشغل\",\"nameEn\":\"Operator\"}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		return JsonPath.read(created.body(), "$.id");
	}

	String station(String operator, String... fuels) throws Exception {
		String list = fuels.length == 0 ? "\"GAS_92\",\"DIESEL\"" : "\"" + String.join("\",\"", fuels) + "\"";
		HttpResponse<String> created = authed("POST", "/api/v1/ops/stations", ops.accessToken(),
				"{\"operatorId\":\"" + operator + "\",\"station\":{\"nameAr\":\"محطة الاختبار\",\"nameEn\":\"Test Station\","
						+ "\"connectivity\":\"NORMAL\",\"fuelTypes\":[" + list + "]}}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		return JsonPath.read(created.body(), "$.id");
	}

	/** A device registered at the station, not yet activated: [id, code, activationCode]. */
	String[] registerDevice(String station) throws Exception {
		HttpResponse<String> registered = authed("POST", "/api/v1/ops/devices", ops.accessToken(),
				"{\"stationId\":\"" + station + "\"}");
		assertThat(registered.statusCode()).as(registered.body()).isEqualTo(201);
		return new String[] { JsonPath.read(registered.body(), "$.device.id"),
				JsonPath.read(registered.body(), "$.device.code"), JsonPath.read(registered.body(), "$.activationCode") };
	}

	/** A new agent at the station: [workerId, pin]. */
	String[] agent(String station) throws Exception {
		HttpResponse<String> created = authed("POST", "/api/v1/ops/agents", ops.accessToken(),
				"{\"stationId\":\"" + station + "\",\"fullName\":\"Agent " + UUID.randomUUID().toString().substring(0, 6)
						+ "\"}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		return new String[] { JsonPath.read(created.body(), "$.agent.workerId"), JsonPath.read(created.body(), "$.pin") };
	}

	static KeyPair keyPair() throws Exception {
		KeyPairGenerator generator = KeyPairGenerator.getInstance("EC");
		generator.initialize(new ECGenParameterSpec("secp256r1"));
		return generator.generateKeyPair();
	}

	static String publicKey(KeyPair keys) {
		return Base64.getEncoder().encodeToString(keys.getPublic().getEncoded());
	}

	HttpResponse<String> activate(String deviceCode, String activationCode, KeyPair keys) throws Exception {
		return post("/api/v1/pos/activate", "{\"deviceCode\":\"" + deviceCode + "\",\"activationCode\":\"" + activationCode
				+ "\",\"publicKey\":\"" + publicKey(keys) + "\"}");
	}

	static String sign(KeyPair keys, String message) throws Exception {
		Signature signature = Signature.getInstance("SHA256withECDSA");
		signature.initSign(keys.getPrivate());
		signature.update(message.getBytes(StandardCharsets.UTF_8));
		return Base64.getEncoder().encodeToString(signature.sign());
	}

	HttpResponse<String> tokenRequest(String deviceId, KeyPair keys, long timestamp, String nonce) throws Exception {
		String message = "zawed-device-token\n" + deviceId + "\n" + timestamp + "\n" + nonce;
		return post("/api/v1/pos/token", "{\"deviceId\":\"" + deviceId + "\",\"timestamp\":" + timestamp + ",\"nonce\":\""
				+ nonce + "\",\"signature\":\"" + sign(keys, message) + "\"}");
	}

	String deviceToken(String deviceId, KeyPair keys) throws Exception {
		HttpResponse<String> issued = tokenRequest(deviceId, keys, Instant.now().getEpochSecond(), nonce());
		assertThat(issued.statusCode()).as(issued.body()).isEqualTo(200);
		return JsonPath.read(issued.body(), "$.token");
	}

	static String nonce() {
		return UUID.randomUUID().toString();
	}

	/** Any POS call: the device token, and the agent session when there is one. */
	HttpResponse<String> pos(String method, String path, String deviceToken, String session, String json)
			throws Exception {
		HttpRequest.Builder builder = request(path).header("Authorization", "Bearer " + deviceToken);
		if (session != null) {
			builder.header("X-Agent-Session", session);
		}
		if (json != null) {
			builder.header("Content-Type", "application/json");
		}
		return send(builder.method(method,
				json == null ? HttpRequest.BodyPublishers.noBody() : HttpRequest.BodyPublishers.ofString(json)));
	}

	HttpResponse<String> agentLogin(String deviceToken, String workerId, String pin) throws Exception {
		return pos("POST", "/api/v1/pos/agents/login", deviceToken, null,
				"{\"workerId\":\"" + workerId + "\",\"pin\":\"" + pin + "\"}");
	}

	/** A pump ready to fuel at a new station of a new operator. */
	Pump pump(String... fuels) throws Exception {
		String operator = operator();
		return pumpAt(operator, station(operator, fuels));
	}

	Pump pumpAt(String operator, String station) throws Exception {
		String[] device = registerDevice(station);
		KeyPair keys = keyPair();
		HttpResponse<String> activated = activate(device[1], device[2], keys);
		assertThat(activated.statusCode()).as(activated.body()).isEqualTo(200);
		String token = deviceToken(device[0], keys);
		String[] agent = agent(station);
		HttpResponse<String> login = agentLogin(token, agent[0], agent[1]);
		assertThat(login.statusCode()).as(login.body()).isEqualTo(200);
		String session = JsonPath.read(login.body(), "$.sessionToken");
		String pin = "482915";
		HttpResponse<String> changed = pos("POST", "/api/v1/pos/agents/pin", token, session,
				"{\"currentPin\":\"" + agent[1] + "\",\"newPin\":\"" + pin + "\"}");
		assertThat(changed.statusCode()).as(changed.body()).isEqualTo(204);
		// Must-change-PIN is read on every request, so the same session may now work.
		return new Pump(device[0], device[1], station, operator, keys, token, agent[0], pin, session);
	}

	// -------------------------------------------------------------------------------------- fleet

	/** A vehicle with an active tag. */
	Car car(String company, String fuel, String tier, Long vipCeilingMl, long tankMl) throws Exception {
		HttpResponse<String> vehicle = authed("POST", "/api/v1/ops/vehicles", ops.accessToken(),
				"{\"companyId\":\"" + company + "\",\"vehicle\":{\"plate\":\"FT " + UUID.randomUUID().toString().substring(0, 8)
						.toUpperCase() + "\",\"make\":\"Toyota\",\"model\":\"Hilux\",\"fuelType\":\"" + fuel
						+ "\",\"tankCapacityMl\":" + tankMl + ",\"tier\":\"" + tier + "\""
						+ (vipCeilingMl == null ? "" : ",\"vipMonthlyCeilingMl\":" + vipCeilingMl) + "}}");
		assertThat(vehicle.statusCode()).as(vehicle.body()).isEqualTo(201);
		String vehicleId = JsonPath.read(vehicle.body(), "$.id");
		String uid = randomUid();
		HttpResponse<String> tag = authed("POST", "/api/v1/ops/vehicles/" + vehicleId + "/tags", ops.accessToken(),
				"{\"uid\":\"" + uid + "\"}");
		assertThat(tag.statusCode()).as(tag.body()).isEqualTo(201);
		return new Car(company, vehicleId, uid);
	}

	Car car(String company) throws Exception {
		return car(company, "GAS_92", "NORMAL", null, 60_000);
	}

	static String randomUid() {
		byte[] bytes = new byte[7];
		ThreadLocalRandom.current().nextBytes(bytes);
		return HexFormat.of().withUpperCase().formatHex(bytes);
	}

	void price(String station, String fuel, long piastres) throws Exception {
		HttpResponse<String> set = authed("POST", "/api/v1/ops/prices", ops.accessToken(),
				"{\"fuelType\":\"" + fuel + "\",\"stationId\":\"" + station + "\",\"pricePiastres\":" + piastres + "}");
		assertThat(set.statusCode()).as(set.body()).isEqualTo(201);
	}

	void credit(String company, long piastres) throws Exception {
		HttpResponse<String> credited = authed("POST", "/api/v1/ops/companies/" + company + "/wallet/credits",
				ops.accessToken(), "{\"amountPiastres\":" + piastres + ",\"bankReference\":\"FT-" + UUID.randomUUID()
						+ "\",\"transferDate\":\"" + LocalDate.now() + "\"}");
		assertThat(credited.statusCode()).as(credited.body()).isEqualTo(201);
	}

	// ----------------------------------------------------------------------------------- fueling

	static String fuelingBody(UUID clientTxnId, String uid, long volumeMl, String fuel) {
		return "{\"clientTxnId\":\"" + clientTxnId + "\",\"tagUid\":\"" + uid + "\",\"volumeMl\":" + volumeMl
				+ (fuel == null ? "" : ",\"fuelType\":\"" + fuel + "\"") + ",\"deviceAt\":\"" + Instant.now()
				+ "\",\"receiptLocale\":\"ar\"}";
	}

	HttpResponse<String> fuel(Pump pump, Car car, long volumeMl) throws Exception {
		return fuel(pump, car, UUID.randomUUID(), volumeMl, null);
	}

	HttpResponse<String> fuel(Pump pump, Car car, UUID clientTxnId, long volumeMl, String fuel) throws Exception {
		return pos("POST", "/api/v1/pos/fuelings", pump.deviceToken(), pump.session(),
				fuelingBody(clientTxnId, car.tagUid(), volumeMl, fuel));
	}

}
