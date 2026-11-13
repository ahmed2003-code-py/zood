package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.time.Instant;
import java.util.Base64;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.support.Owner;

/**
 * The device channel (B4 contract §1, ADR 0010): activation, device tokens, agent sign-in, and the
 * separation of device and user credentials.
 */
class PosChannelTests extends PosTestSupport {

	// --------------------------------------------------------------------------------- activation

	@Test
	void anActivationCodeWorksOnceAndBindsTheDeviceKey() throws Exception {
		String[] device = registerDevice(station(operator()));
		KeyPair keys = keyPair();

		HttpResponse<String> activated = activate(device[1], device[2], keys);
		assertThat(activated.statusCode()).as(activated.body()).isEqualTo(200);
		assertThat(activated.headers().firstValue("Cache-Control").orElse("")).contains("no-store");
		assertThat(JsonPath.<String>read(activated.body(), "$.deviceId")).isEqualTo(device[0]);
		assertThat(Owner.<String>queryOne("select status from pos_devices where id = ?::uuid", device[0]))
			.isEqualTo("ACTIVE");
		assertThat(Owner.<Object>queryOne("select activation_code_hash from pos_devices where id = ?::uuid", device[0]))
			.isNull();

		// Used: the same code, even with the same key, is refused; so is another key.
		assertThat(code(activate(device[1], device[2], keys))).isEqualTo("ACTIVATION_FAILED");
		assertThat(code(activate(device[1], device[2], keyPair()))).isEqualTo("ACTIVATION_FAILED");
		assertThat(Owner.<Long>queryOne("select count(*) from audit_log where entity_id = ?::uuid and action = 'DEVICE_ACTIVATED'",
				device[0])).isEqualTo(1L);
	}

	@Test
	void fiveWrongCodesKillTheActivationCode() throws Exception {
		String[] device = registerDevice(station(operator()));
		KeyPair keys = keyPair();
		for (int i = 0; i < 5; i++) {
			HttpResponse<String> wrong = activate(device[1], "WRONGCODE" + i + "XY", keys);
			assertThat(wrong.statusCode()).isEqualTo(401);
			assertThat(code(wrong)).isEqualTo("ACTIVATION_FAILED");
		}
		assertThat(code(activate(device[1], device[2], keys))).as("the right code, too late").isEqualTo("ACTIVATION_FAILED");
	}

	@Test
	void anUnknownDeviceAndAnExpiredCodeAnswerExactlyLikeAWrongCode() throws Exception {
		String[] device = registerDevice(station(operator()));
		Owner.update("update pos_devices set activation_expires_at = now() - interval '1 second' where id = ?::uuid",
				device[0]);
		HttpResponse<String> expired = activate(device[1], device[2], keyPair());
		HttpResponse<String> unknown = activate("POS-NOSUCH00", device[2], keyPair());
		assertThat(expired.statusCode()).isEqualTo(401);
		assertThat(unknown.statusCode()).isEqualTo(401);
		assertThat(code(expired)).isEqualTo(code(unknown)).isEqualTo("ACTIVATION_FAILED");
	}

	@Test
	void onlyAP256KeyIsAccepted() throws Exception {
		String[] device = registerDevice(station(operator()));
		KeyPairGenerator rsa = KeyPairGenerator.getInstance("RSA");
		rsa.initialize(2048);
		HttpResponse<String> response = post("/api/v1/pos/activate", "{\"deviceCode\":\"" + device[1]
				+ "\",\"activationCode\":\"" + device[2] + "\",\"publicKey\":\""
				+ Base64.getEncoder().encodeToString(rsa.generateKeyPair().getPublic().getEncoded()) + "\"}");
		assertThat(response.statusCode()).isEqualTo(400);
		assertThat(code(response)).isEqualTo("INVALID_DEVICE_KEY");
		// The code was not spent on a bad key.
		assertThat(activate(device[1], device[2], keyPair()).statusCode()).isEqualTo(200);
	}

	// ------------------------------------------------------------------------------ device tokens

	@Test
	void aTokenNeedsTheDeviceKeyAFreshTimestampAndANewNonce() throws Exception {
		String[] device = registerDevice(station(operator()));
		KeyPair keys = keyPair();
		assertThat(activate(device[1], device[2], keys).statusCode()).isEqualTo(200);
		long now = Instant.now().getEpochSecond();

		String nonce = nonce();
		HttpResponse<String> issued = tokenRequest(device[0], keys, now, nonce);
		assertThat(issued.statusCode()).as(issued.body()).isEqualTo(200);
		assertThat(issued.headers().firstValue("Cache-Control").orElse("")).contains("no-store");

		HttpResponse<String> replay = tokenRequest(device[0], keys, now, nonce);
		assertThat(replay.statusCode()).isEqualTo(401);
		assertThat(code(replay)).isEqualTo("DEVICE_REPLAY");

		HttpResponse<String> otherKey = tokenRequest(device[0], keyPair(), now, nonce());
		assertThat(code(otherKey)).isEqualTo("DEVICE_UNAUTHENTICATED");

		HttpResponse<String> stale = tokenRequest(device[0], keys, now - 600, nonce());
		assertThat(code(stale)).isEqualTo("DEVICE_CLOCK_SKEW");
		assertThat(JsonPath.<Object>read(stale.body(), "$.params.serverTime")).isNotNull();
	}

	@Test
	void aPendingDeviceGetsNoToken() throws Exception {
		String[] device = registerDevice(station(operator()));
		HttpResponse<String> refused = tokenRequest(device[0], keyPair(), Instant.now().getEpochSecond(), nonce());
		assertThat(refused.statusCode()).isEqualTo(401);
		assertThat(code(refused)).isEqualTo("DEVICE_UNAUTHENTICATED");
	}

	@Test
	void deviceAndUserTokensNeverStandInForEachOther() throws Exception {
		Pump pump = pump();
		// A user's access token on a POS route, and a device token on a user route.
		HttpResponse<String> userOnPos = pos("POST", "/api/v1/pos/agents/login", ops.accessToken(), null,
				"{\"workerId\":\"" + pump.workerId() + "\",\"pin\":\"" + pump.pin() + "\"}");
		assertThat(userOnPos.statusCode()).isEqualTo(401);
		assertThat(code(userOnPos)).isEqualTo("DEVICE_UNAUTHENTICATED");
		HttpResponse<String> deviceOnUser = authed("GET", "/api/v1/auth/me", pump.deviceToken(), null);
		assertThat(deviceOnUser.statusCode()).isEqualTo(401);
		HttpResponse<String> deviceOnOps = authed("GET", "/api/v1/ops/fuelings", pump.deviceToken(), null);
		assertThat(deviceOnOps.statusCode()).isEqualTo(401);
	}

	@Test
	void revokingADeviceStopsItsTokenAtOnce() throws Exception {
		Pump pump = pump();
		assertThat(authed("POST", "/api/v1/ops/devices/" + pump.deviceId() + "/revoke", ops.accessToken(),
				"{\"reason\":\"Stolen\"}").statusCode()).isEqualTo(200);
		HttpResponse<String> refused = pos("POST", "/api/v1/pos/fuelings", pump.deviceToken(), pump.session(),
				fuelingBody(UUID.randomUUID(), randomUid(), 1000, null));
		assertThat(refused.statusCode()).isEqualTo(401);
		assertThat(code(refused)).isEqualTo("DEVICE_UNAUTHENTICATED");
		assertThat(code(tokenRequest(pump.deviceId(), pump.keys(), Instant.now().getEpochSecond(), nonce())))
			.isEqualTo("DEVICE_UNAUTHENTICATED");
	}

	// ------------------------------------------------------------------------------ agent sign-in

	@Test
	void aNewAgentMustChangeThePinBeforeFueling() throws Exception {
		String operator = operator();
		String station = station(operator);
		Pump pump = pumpAt(operator, station);
		String[] agent = agent(station);
		HttpResponse<String> login = agentLogin(pump.deviceToken(), agent[0], agent[1]);
		assertThat(login.statusCode()).isEqualTo(200);
		assertThat(login.headers().firstValue("Cache-Control").orElse("")).contains("no-store");
		assertThat(JsonPath.<Boolean>read(login.body(), "$.agent.mustChangePin")).isTrue();
		String session = JsonPath.read(login.body(), "$.sessionToken");

		HttpResponse<String> blocked = pos("POST", "/api/v1/pos/fuelings", pump.deviceToken(), session,
				fuelingBody(UUID.randomUUID(), randomUid(), 1000, null));
		assertThat(blocked.statusCode()).isEqualTo(403);
		assertThat(code(blocked)).isEqualTo("PIN_CHANGE_REQUIRED");

		HttpResponse<String> weak = pos("POST", "/api/v1/pos/agents/pin", pump.deviceToken(), session,
				"{\"currentPin\":\"" + agent[1] + "\",\"newPin\":\"123456\"}");
		assertThat(code(weak)).isEqualTo("PIN_TOO_WEAK");
		assertThat(pos("POST", "/api/v1/pos/agents/pin", pump.deviceToken(), session,
				"{\"currentPin\":\"" + agent[1] + "\",\"newPin\":\"730518\"}").statusCode()).isEqualTo(204);
		HttpResponse<String> now = pos("POST", "/api/v1/pos/fuelings", pump.deviceToken(), session,
				fuelingBody(UUID.randomUUID(), randomUid(), 1000, null));
		assertThat(code(now)).as("past the PIN gate; the tag is unknown").isEqualTo("TAG_NOT_RECOGNISED");
	}

	@Test
	void aDeviceWithoutAnAgentCannotFuel() throws Exception {
		Pump pump = pump();
		HttpResponse<String> refused = pos("POST", "/api/v1/pos/fuelings", pump.deviceToken(), null,
				fuelingBody(UUID.randomUUID(), randomUid(), 1000, null));
		assertThat(refused.statusCode()).isEqualTo(401);
		assertThat(code(refused)).isEqualTo("AGENT_SESSION_REQUIRED");
		HttpResponse<String> forged = pos("POST", "/api/v1/pos/fuelings", pump.deviceToken(), "not-a-session",
				fuelingBody(UUID.randomUUID(), randomUid(), 1000, null));
		assertThat(code(forged)).isEqualTo("AGENT_SESSION_INVALID");
	}

	@Test
	void fiveWrongPinsLockTheAgent() throws Exception {
		Pump pump = pump();
		for (int i = 0; i < 4; i++) {
			assertThat(code(agentLogin(pump.deviceToken(), pump.workerId(), "000000"))).isEqualTo("AGENT_SIGN_IN_FAILED");
		}
		HttpResponse<String> fifth = agentLogin(pump.deviceToken(), pump.workerId(), "000000");
		assertThat(fifth.statusCode()).isEqualTo(423);
		assertThat(code(fifth)).isEqualTo("AGENT_LOCKED");
		HttpResponse<String> right = agentLogin(pump.deviceToken(), pump.workerId(), pump.pin());
		assertThat(code(right)).as("the right PIN while locked").isEqualTo("AGENT_LOCKED");
		assertThat(Owner.<Long>queryOne("select count(*) from audit_log where action = 'AGENT_LOCKED' and entity_id = "
				+ "(select id from agents where worker_id = ?)", pump.workerId())).isEqualTo(1L);
	}

	@Test
	void anAgentSignsInOnlyAtTheirOwnStation() throws Exception {
		Pump here = pump();
		Pump elsewhere = pump();
		HttpResponse<String> refused = agentLogin(here.deviceToken(), elsewhere.workerId(), elsewhere.pin());
		assertThat(refused.statusCode()).isEqualTo(401);
		assertThat(code(refused)).as("same answer as a wrong PIN").isEqualTo("AGENT_SIGN_IN_FAILED");
	}

	@Test
	void signingInOnAnotherDeviceEndsTheFirstSession() throws Exception {
		String operator = operator();
		String station = station(operator);
		Pump first = pumpAt(operator, station);
		// A second device at the same station; the first pump's agent signs in there.
		String[] device = registerDevice(station);
		KeyPair keys = keyPair();
		assertThat(activate(device[1], device[2], keys).statusCode()).isEqualTo(200);
		String token = deviceToken(device[0], keys);
		assertThat(agentLogin(token, first.workerId(), first.pin()).statusCode()).isEqualTo(200);

		HttpResponse<String> old = pos("POST", "/api/v1/pos/fuelings", first.deviceToken(), first.session(),
				fuelingBody(UUID.randomUUID(), randomUid(), 1000, null));
		assertThat(code(old)).isEqualTo("AGENT_SESSION_INVALID");
		assertThat(Owner.<Long>queryOne("select count(*) from agent_sessions where agent_id = "
				+ "(select id from agents where worker_id = ?) and revoked_at is null", first.workerId())).isEqualTo(1L);
	}

	@Test
	void aSuspendedAgentStopsAtOnceAndSignOutEndsTheSession() throws Exception {
		Pump pump = pump();
		String agentId = Owner.<UUID>queryOne("select id from agents where worker_id = ?", pump.workerId()).toString();
		assertThat(authed("POST", "/api/v1/ops/agents/" + agentId + "/status", ops.accessToken(),
				"{\"status\":\"SUSPENDED\",\"reason\":\"Investigation\"}").statusCode()).isEqualTo(200);
		assertThat(code(pos("POST", "/api/v1/pos/fuelings", pump.deviceToken(), pump.session(),
				fuelingBody(UUID.randomUUID(), randomUid(), 1000, null)))).isEqualTo("AGENT_SESSION_INVALID");
		assertThat(code(agentLogin(pump.deviceToken(), pump.workerId(), pump.pin()))).isEqualTo("AGENT_DISABLED");

		Pump other = pump();
		assertThat(pos("POST", "/api/v1/pos/agents/logout", other.deviceToken(), other.session(), null).statusCode())
			.isEqualTo(204);
		assertThat(code(pos("POST", "/api/v1/pos/fuelings", other.deviceToken(), other.session(),
				fuelingBody(UUID.randomUUID(), randomUid(), 1000, null)))).isEqualTo("AGENT_SESSION_INVALID");
	}

	@Test
	void sessionTokensAreStoredOnlyAsHashes() throws Exception {
		Pump pump = pump();
		assertThat(Owner.<Long>queryOne("select count(*) from agent_sessions where token_hash = ?", pump.session()))
			.isZero();
		assertThat(Owner.<Long>queryOne("select count(*) from audit_log where metadata::text like ?",
				"%" + pump.session() + "%")).isZero();
	}

}
