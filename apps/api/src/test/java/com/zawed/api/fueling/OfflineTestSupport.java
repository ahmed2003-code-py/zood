package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.sql.Timestamp;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.UUID;
import java.util.stream.Collectors;

import com.jayway.jsonpath.JsonPath;

import com.zawed.api.support.Owner;

/**
 * What a terminal does after it was offline (B5 contract §3): it holds captures and sends them in a
 * batch with its device token alone. Also the two things only history can give a test: a shift that
 * is already over, and a price that was in force before today.
 */
abstract class OfflineTestSupport extends PosTestSupport {

	/** One capture in the device's queue. */
	record Capture(UUID clientTxnId, String tagUid, long volumeMl, String fuel, Instant deviceAt, String agentId) {

		Capture at(Instant when) {
			return new Capture(clientTxnId, tagUid, volumeMl, fuel, when, agentId);
		}

		Capture fuel(String selected) {
			return new Capture(clientTxnId, tagUid, volumeMl, selected, deviceAt, agentId);
		}

		Capture by(String otherAgentId) {
			return new Capture(clientTxnId, tagUid, volumeMl, fuel, deviceAt, otherAgentId);
		}

		Capture volume(long otherVolumeMl) {
			return new Capture(clientTxnId, tagUid, otherVolumeMl, fuel, deviceAt, agentId);
		}

		String json() {
			return "{\"clientTxnId\":\"" + clientTxnId + "\",\"tagUid\":\"" + tagUid + "\",\"volumeMl\":" + volumeMl
					+ (fuel == null ? "" : ",\"fuelType\":\"" + fuel + "\"") + ",\"deviceAt\":\"" + deviceAt
					+ "\",\"agentId\":\"" + agentId + "\",\"receiptLocale\":\"ar\"}";
		}

	}

	static String agentId(Pump pump) {
		return Owner.<UUID>queryOne("select id from agents where worker_id = ?", pump.workerId()).toString();
	}

	/** A capture made now by the pump's agent, who is signed in on it. */
	static Capture capture(Pump pump, Car car, long volumeMl) {
		return new Capture(UUID.randomUUID(), car.tagUid(), volumeMl, null, Instant.now(), agentId(pump));
	}

	/** Sends a batch as the device alone: no agent session, as after a shift. */
	HttpResponse<String> sync(Pump pump, Capture... captures) throws Exception {
		return sync(pump, UUID.randomUUID(), 0, List.of(captures));
	}

	HttpResponse<String> sync(Pump pump, UUID batchId, int pending, List<Capture> captures) throws Exception {
		return pos("POST", "/api/v1/pos/sync", pump.deviceToken(), null, batchBody(batchId, pending, captures));
	}

	static String batchBody(UUID batchId, int pending, List<Capture> captures) {
		return "{\"batchId\":\"" + batchId + "\",\"pending\":" + pending + ",\"items\":["
				+ captures.stream().map(Capture::json).collect(Collectors.joining(",")) + "]}";
	}

	/** The results of a batch that was answered, in item order. */
	static List<String> results(HttpResponse<String> response) {
		assertThat(response.statusCode()).as(response.body()).isEqualTo(200);
		return JsonPath.read(response.body(), "$.results[*].result");
	}

	static String rejection(HttpResponse<String> response, int index) {
		assertThat(JsonPath.<String>read(response.body(), "$.results[" + index + "].result")).as(response.body())
			.isEqualTo("REJECTED");
		return JsonPath.read(response.body(), "$.results[" + index + "].code");
	}

	static List<String> conflicts(HttpResponse<String> response, int index) {
		assertThat(JsonPath.<String>read(response.body(), "$.results[" + index + "].result")).as(response.body())
			.isEqualTo("RECORDED");
		return JsonPath.read(response.body(), "$.results[" + index + "].fueling.conflicts");
	}

	static String fuelingId(HttpResponse<String> response, int index) {
		return JsonPath.read(response.body(), "$.results[" + index + "].fueling.id");
	}

	/**
	 * A shift of the pump's agent on its device that is over, as the session history holds it. A
	 * capture dated inside it is one the agent made then and the device kept until now.
	 */
	static void pastShift(Pump pump, Instant from, Instant to) {
		Owner.update("""
				insert into agent_sessions (id, agent_id, device_id, operator_id, station_id, token_hash, created_at,
				                            last_seen_at, expires_at, revoked_at, revoke_reason)
				values (?, ?::uuid, ?::uuid, ?::uuid, ?::uuid, ?, ?, ?, ?, ?, 'SIGNED_OUT')
				""", UUID.randomUUID(), agentId(pump), pump.deviceId(), pump.operatorId(), pump.stationId(),
				"past-" + UUID.randomUUID(), Timestamp.from(from), Timestamp.from(to),
				Timestamp.from(from.plus(Duration.ofHours(12))), Timestamp.from(to));
	}

	/** A station price that took effect in the past (the API only schedules from now on). */
	static void priceSince(String station, String fuel, long piastres, Instant since) {
		Owner.update("""
				insert into fuel_prices (id, fuel_type, station_id, price_piastres, effective_from, created_at)
				values (?, ?, ?::uuid, ?, ?, now())
				""", UUID.randomUUID(), fuel, station, piastres, Timestamp.from(since));
	}

	// ------------------------------------------------------------------------------- the books

	static long walletBalance(String company) {
		return Owner.<Long>queryOne("select balance_piastres from company_wallets where company_id = ?::uuid", company);
	}

	/** Credits minus debits on one ledger account, from its entries; 0 when it was never opened. */
	static long accountBalance(String code) {
		return Owner.<Long>queryOne("""
				select coalesce(sum(case e.direction when 'CREDIT' then e.amount_piastres else -e.amount_piastres end), 0)::bigint
				from ledger_entries e join ledger_accounts a on a.id = e.account_id where a.code = ?
				""", code);
	}

	static long walletLedger(String company) {
		return accountBalance("company:" + company + ":wallet");
	}

	/** What the company owes: the debit balance of its receivable. */
	static long owed(String company) {
		return -accountBalance("company:" + company + ":receivable");
	}

	static long stationPayable(String station) {
		return accountBalance("station:" + station + ":payable");
	}

	static long count(String sql, Object... params) {
		return Owner.<Long>queryOne(sql, params);
	}

}
