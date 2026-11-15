package com.zawed.api.fueling;

import static com.zawed.api.platform.persistence.Sql.ts;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.function.Supplier;
import java.util.regex.Pattern;

import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.fueling.FuelingEngine.OfflineRequest;
import com.zawed.api.fueling.FuelingEngine.Result;
import com.zawed.api.fueling.Fuelings.Fueling;
import com.zawed.api.network.PosPrincipal;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.ratelimit.RateLimiter;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.web.ApiException;

/**
 * A batch of offline captures from one device (B5 contract §3). Each item is recorded by the engine
 * in its own transaction, so an item that cannot be recorded never holds back the others, and a
 * batch sent again changes nothing: every item the server already has comes back {@code DUPLICATE}.
 *
 * <p>An item that is refused is fuel that left the pump without a record. It raises a
 * {@code SYNC_REJECTED} alert for Operations, once however often the device repeats it, with the
 * last four characters of the tag's UID and never the UID itself.
 *
 * <p>A device has no user. The rejection alert and the batch row are written in the platform scope,
 * for the device the POS chain has just verified and naming only that device, its station and its
 * operator (an escalation listed in {@code ArchitectureTests}).
 */
@Service
class OfflineSync {

	static final int MAX_ITEMS = 100;

	/**
	 * A pump captures a fueling every minute or two, so a day offline is ten batches. This is far
	 * above that and stops a runaway terminal from keeping the engine busy.
	 */
	private static final int BATCHES_PER_MINUTE = 30;

	/**
	 * Why an item is refused (B5 contract §3). Each is a fact about the item that sending it again
	 * will not change. Anything else an item runs into is a fault on our side, not a rejection: the
	 * request fails, the device keeps its queue and sends it again.
	 */
	static final Set<String> REJECTIONS = Set.of("TAG_NOT_RECOGNISED", "AGENT_NOT_ON_DEVICE", "DEVICE_CLOCK_SKEW",
			"CLIENT_TXN_CONFLICT", "IDEMPOTENCY_MISMATCH", "INVALID_VOLUME", "DECLINED_ONLINE");

	/** A UID as the fleet accepts it; anything else has no "last four" worth showing. */
	private static final Pattern UID = Pattern.compile("[0-9A-F]{8,32}");

	enum Outcome {

		/** Recorded now. */
		RECORDED,
		/** This device sent the same capture before; the stored fueling is returned. */
		DUPLICATE,
		/** Nothing was recorded; the code says why. */
		REJECTED

	}

	/**
	 * @param pending what the device still holds after this batch, as it reported it
	 */
	record Batch(UUID batchId, int pending, List<OfflineRequest> items) {
	}

	/**
	 * @param code set when rejected
	 * @param fueling set when recorded or duplicate
	 */
	record ItemResult(UUID clientTxnId, Outcome outcome, String code, Fueling fueling) {
	}

	record Received(UUID batchId, List<ItemResult> results) {
	}

	private final FuelingEngine engine;
	private final Alerts alerts;
	private final JdbcClient jdbc;
	private final IdGenerator ids;
	private final RateLimiter limiter;
	private final TransactionTemplate tx;
	private final Clock clock;

	OfflineSync(FuelingEngine engine, Alerts alerts, JdbcClient jdbc, IdGenerator ids, RateLimiter limiter,
			TransactionTemplate tx, Clock clock) {
		this.engine = engine;
		this.alerts = alerts;
		this.jdbc = jdbc;
		this.ids = ids;
		this.limiter = limiter;
		this.tx = tx;
		this.clock = clock;
	}

	/**
	 * Records the batch, item by item and in order, and answers one result per item in that order.
	 *
	 * @param pos the device behind the request, from its token. An agent signed in on it now is
	 * ignored: each capture names its own agent, checked against the session history
	 */
	Received receive(PosPrincipal pos, Batch batch) {
		PosPrincipal device = pos.deviceOnly();
		limiter.acquire("pos-sync:" + device.deviceId(), BATCHES_PER_MINUTE, Duration.ofMinutes(1));
		if (batch.items().size() > MAX_ITEMS) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "BATCH_TOO_LARGE",
					"A batch holds at most " + MAX_ITEMS + " fuelings", "items", Map.of("max", MAX_ITEMS));
		}
		Instant receivedAt = clock.instant();
		List<ItemResult> results = new ArrayList<>();
		for (OfflineRequest item : batch.items()) {
			results.add(record(device, batch.batchId(), item));
		}
		asPlatform(() -> {
			log(device, batch, receivedAt, results);
			return null;
		});
		return new Received(batch.batchId(), List.copyOf(results));
	}

	private ItemResult record(PosPrincipal device, UUID batchId, OfflineRequest item) {
		Result result;
		try {
			result = engine.recordOffline(device, item);
		}
		catch (ApiException ex) {
			if (!REJECTIONS.contains(ex.code())) {
				throw new IllegalStateException("Offline capture " + item.clientTxnId() + " failed with " + ex.code(), ex);
			}
			// The item's own transaction rolled back and recorded nothing; the alert is its own.
			asPlatform(() -> {
				rejected(device, batchId, item, ex);
				return null;
			});
			return new ItemResult(item.clientTxnId(), Outcome.REJECTED, ex.code(), null);
		}
		return new ItemResult(item.clientTxnId(), result.replayed() ? Outcome.DUPLICATE : Outcome.RECORDED, null,
				result.fueling());
	}

	/**
	 * Fuel was dispensed that the system could not record: Operations must know (B5 contract §4).
	 * For Operations only; a company is not shown a capture that could not be tied to it. The
	 * database keeps one alert per device, clientTxnId and code (V8), so a batch sent again does not
	 * raise it again.
	 */
	private void rejected(PosPrincipal device, UUID batchId, OfflineRequest item, ApiException reason) {
		Map<String, Object> details = new LinkedHashMap<>();
		details.put("code", reason.code());
		details.put("clientTxnId", item.clientTxnId().toString());
		details.put("uidLast4", uidLast4(item.tagUid()));
		details.put("deviceCode", device.deviceCode());
		details.put("batchId", batchId.toString());
		details.put("deviceAt", item.deviceAt().toString());
		details.put("volumeMl", item.volumeMl());
		details.put("agentId", item.agentId().toString());
		if (item.fuelType() != null) {
			details.put("fuelType", item.fuelType().name());
		}
		if (reason.params() != null) {
			reason.params().forEach(details::putIfAbsent);
		}
		alerts.raise(Alerts.Type.SYNC_REJECTED, Alerts.Severity.CRITICAL,
				new Alerts.Subject(null, device.operatorId(), device.stationId(), device.deviceId(), null, null), details,
				false);
	}

	/**
	 * The last four characters of a tag's UID, the most any record outside the fleet's own encrypted
	 * column ever holds (plan §6.9). A string that is not a UID gives nothing: its tail could be
	 * most of it.
	 */
	static String uidLast4(String rawUid) {
		String uid = rawUid == null ? "" : rawUid.replaceAll("[\\s:\\-]", "").toUpperCase(Locale.ROOT);
		return UID.matcher(uid).matches() ? uid.substring(uid.length() - 4) : null;
	}

	/** One row per request received, for the sync monitor (B5 contract §5); a batch sent again is logged again. */
	private void log(PosPrincipal device, Batch batch, Instant receivedAt, List<ItemResult> results) {
		jdbc.sql("""
				insert into sync_batches (id, device_batch_id, device_id, operator_id, station_id, received_at, item_count,
				                          recorded, duplicates, rejected, with_conflicts, pending_reported)
				values (:id, :batch, :device, :operator, :station, :receivedAt, :items, :recorded, :duplicates, :rejected,
				        :withConflicts, :pending)
				""")
			.param("id", ids.next())
			.param("batch", batch.batchId())
			.param("device", device.deviceId())
			.param("operator", device.operatorId())
			.param("station", device.stationId())
			.param("receivedAt", ts(receivedAt))
			.param("items", results.size())
			.param("recorded", count(results, Outcome.RECORDED))
			.param("duplicates", count(results, Outcome.DUPLICATE))
			.param("rejected", count(results, Outcome.REJECTED))
			// Newly recorded with a conflict: a duplicate's conflicts were counted when it was recorded.
			.param("withConflicts",
					(int) results.stream()
						.filter((r) -> r.outcome() == Outcome.RECORDED && !r.fueling().conflicts().isEmpty())
						.count())
			.param("pending", batch.pending())
			.update();
	}

	private static int count(List<ItemResult> results, Outcome outcome) {
		return (int) results.stream().filter((r) -> r.outcome() == outcome).count();
	}

	private <T> T asPlatform(Supplier<T> work) {
		return DbScope.runAs(new DbScope.Context(Scope.PLATFORM, null), () -> tx.execute((status) -> work.get()));
	}

}
