package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.time.Instant;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import io.micrometer.core.instrument.Counter;
import io.micrometer.core.instrument.MeterRegistry;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;

import com.zawed.api.support.Owner;

/**
 * Recording a fueling never depends on a notification (B7 W5 review, Medium 1). The notices a fueling
 * causes (a low balance, a hard alert) are written in a savepoint: when the database refuses one, the
 * fueling, its posting and its alert are still recorded, online and offline, and a rejected capture is
 * still answered as rejected. The refusal is counted, never silent.
 */
class NotificationFailureTests extends OfflineTestSupport {

	@Autowired
	MeterRegistry meters;

	@Test
	void aFuelingIsRecordedWhenItsNotificationsCannotBeWritten() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String online = seedCompany(50_000).toString();
		String offline = seedCompany(50_000).toString();
		credit(online, 60_000);
		credit(offline, 60_000);
		Car onlineCar = car(online);
		Car offlineCar = car(offline);
		double before = dropped("WALLET_LOW_BALANCE") + dropped("HARD_ALERT_RAISED");

		try (Refusal refusal = refuseNotificationsAbout(List.of(online, offline), pump.stationId())) {
			// Online: 60,000 → 45,000 crosses the threshold, which raises a notice and a hard alert.
			HttpResponse<String> fueled = fuel(pump, onlineCar, 10_000);
			assertThat(fueled.statusCode()).as(fueled.body()).isEqualTo(201);
			assertThat(JsonPath.<String>read(fueled.body(), "$.status")).isEqualTo("COMPLETED");
			assertThat(walletBalance(online)).isEqualTo(45_000);
			assertThat(alerts(online)).isEqualTo(1);

			// Offline: the same crossing inside a sync item, and a capture that cannot be recorded at all.
			Capture recorded = capture(pump, offlineCar, 10_000);
			Capture unknownTag = new Capture(UUID.randomUUID(), randomUid(), 10_000, null, Instant.now(), agentId(pump));
			UUID batch = UUID.randomUUID();
			HttpResponse<String> answer = sync(pump, batch, 0, List.of(recorded, unknownTag));
			assertThat(results(answer)).containsExactly("RECORDED", "REJECTED");
			assertThat(walletBalance(offline)).isEqualTo(45_000);
			assertThat(alerts(offline)).isEqualTo(1);
			assertThat(Owner.<Long>queryOne("select count(*) from alerts where type = 'SYNC_REJECTED' and device_id = ?::uuid",
					pump.deviceId())).isEqualTo(1L);
			assertThat(Owner.<Long>queryOne("select count(*) from sync_batches where device_batch_id = ?", batch))
				.as("the batch was answered and logged, not turned into a 500").isEqualTo(1L);

			assertThat(refusal.refused()).as("nothing about these was written").isZero();
		}

		// Two notices per crossing (online and offline) and one for the rejected capture's alert.
		double after = dropped("WALLET_LOW_BALANCE") + dropped("HARD_ALERT_RAISED");
		assertThat(after - before).isEqualTo(5);
	}

	@Test
	void withoutARefusalTheSameFuelingWritesItsNotifications() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany(50_000).toString();
		credit(company, 60_000);
		assertThat(fuel(pump, car(company), 10_000).statusCode()).isEqualTo(201);
		assertThat(Owner.<Long>queryOne("""
				select count(*) from notifications
				where (type = 'WALLET_LOW_BALANCE' and entity_id = ?::uuid)
				   or (type = 'HARD_ALERT_RAISED' and params ->> 'companyId' = ?)
				""", company, company)).isEqualTo(2L);
	}

	// ------------------------------------------------------------------------------------ helpers

	private double dropped(String type) {
		Counter counter = meters.find("zawed.notifications.dropped").tag("type", type).counter();
		return counter == null ? 0 : counter.count();
	}

	private static long alerts(String company) {
		return Owner.<Long>queryOne("select count(*) from alerts where company_id = ?::uuid and type = 'LOW_BALANCE'",
				company);
	}

	/** A trigger that refuses every notification about these companies or this station, until closed. */
	private static Refusal refuseNotificationsAbout(List<String> companies, String station) {
		String name = "test_refuse_" + UUID.randomUUID().toString().replace("-", "");
		String ids = String.join(",", companies.stream().map((c) -> "'" + UUID.fromString(c) + "'").toList());
		Owner.update("create function " + name + "() returns trigger language plpgsql as $$ begin "
				+ "raise exception 'notification refused by the test'; end $$");
		Owner.update("create trigger " + name + " before insert on notifications for each row when ("
				+ "new.entity_id in (" + ids + ") or new.params ->> 'companyId' in (" + ids + ") "
				+ "or new.params ->> 'stationId' = '" + UUID.fromString(station) + "') execute function " + name + "()");
		return new Refusal(name, companies, station);
	}

	private record Refusal(String name, List<String> companies, String station) implements AutoCloseable {

		/** What a fueling would have caused about them (the earlier top-up notices are not counted). */
		long refused() {
			String fuelingNotices = "type in ('WALLET_LOW_BALANCE', 'HARD_ALERT_RAISED') and ";
			long count = 0;
			for (String company : companies) {
				count += Owner.<Long>queryOne("select count(*) from notifications where " + fuelingNotices
						+ "(entity_id = ?::uuid or params ->> 'companyId' = ?)", company, company);
			}
			return count + Owner.<Long>queryOne(
					"select count(*) from notifications where " + fuelingNotices + "params ->> 'stationId' = ?", station);
		}

		@Override
		public void close() {
			Owner.update("drop trigger " + name + " on notifications");
			Owner.update("drop function " + name + "()");
		}

	}

}
