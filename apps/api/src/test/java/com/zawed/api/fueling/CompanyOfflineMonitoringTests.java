package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

import org.junit.jupiter.api.Test;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * Company-wide offline monitoring (V14, ADR 0025): a disconnected terminal cannot know what other
 * terminals spent, so the server raises one alert when a company's offline fuelings across every
 * device in 24 hours cross the threshold. It never blocks or changes a fueling.
 */
@TestPropertySource(properties = { "zawed.finance.bank-change-hold-hours=0",
		"zawed.finance.payout-approval-threshold-piastres=100000", "zawed.sync.offline-exposure-cap-piastres=10000",
		"zawed.sync.company-offline-alert-piastres=40000" })
class CompanyOfflineMonitoringTests extends OfflineTestSupport {

	@Test
	void crossingTheCompanyThresholdAcrossDevicesRaisesOneAlert() throws Exception {
		String company = seedCompany().toString();
		credit(company, 1_000_000);
		Pump first = pump();
		Pump second = pump();
		price(first.stationId(), "GAS_92", PRICE);
		price(second.stationId(), "GAS_92", PRICE);

		sync(first, capture(first, car(company), 20_000)); // 30,000: under the 40,000 threshold
		assertThat(alerts(company)).isZero();
		sync(second, capture(second, car(company), 20_000)); // 60,000 across two devices: crosses it
		assertThat(alerts(company)).isEqualTo(1L);
		sync(first, capture(first, car(company), 10_000)); // still above: no second alert
		assertThat(alerts(company)).isEqualTo(1L);

		// Monitoring only: every capture was recorded and completed.
		assertThat(Owner.<Long>queryOne(
				"select count(*) from fuelings where company_id = ? and origin = 'OFFLINE' and status = 'COMPLETED'",
				UUID.fromString(company))).isEqualTo(3L);
	}

	@Test
	void capturesFromManyDevicesSyncedAtOnceRaiseTheAlertExactlyOnce() throws Exception {
		// Review finding (low): "the company alert can be missed or duplicated under concurrency". It
		// cannot: every fueling of a company takes the company's wallet row lock first (ADR 0018), and the
		// 24-hour sum and the crossing decision are read after it, under READ COMMITTED, so each capture
		// sees every capture committed before it. Any two of these captures cross the threshold, so
		// without that serialisation two parallel ones would both see only themselves (missed) or both
		// see each other (duplicated).
		String company = seedCompany().toString();
		credit(company, 1_000_000);
		int devices = 6;
		List<Pump> pumps = new ArrayList<>();
		List<Capture> captures = new ArrayList<>();
		for (int i = 0; i < devices; i++) {
			Pump pump = pump();
			price(pump.stationId(), "GAS_92", PRICE);
			pumps.add(pump);
			captures.add(capture(pump, car(company), 20_000)); // 30,000 each
		}

		List<Integer> statuses = Concurrently.run(devices, (i) -> sync(pumps.get(i), captures.get(i)).statusCode());
		assertThat(statuses).containsOnly(200);
		assertThat(Owner.<Long>queryOne("select count(*) from fuelings where company_id = ? and origin = 'OFFLINE'",
				UUID.fromString(company))).isEqualTo((long) devices);

		assertThat(alerts(company)).isEqualTo(1L);
		// Raised by the second capture to commit, which saw exactly one before it: 2 × 30,000.
		assertThat(Owner.<Long>queryOne("""
				select (details ->> 'offline24hPiastres')::bigint from alerts
				where type = 'COMPANY_OFFLINE_EXPOSURE' and company_id = ?""", UUID.fromString(company)))
			.isEqualTo(60_000L);
	}

	@Test
	void aLateCaptureFromOutsideTheWindowNeverRaisesItAgain() throws Exception {
		// A capture dated two days ago is outside the 24-hour window it is summed in, so it cannot be the
		// one that crosses the threshold. Before V17's fix it was subtracted from a sum it was never part
		// of, and raised the alert a second time while the company was already above the line.
		String company = seedCompany().toString();
		credit(company, 1_000_000);
		Pump first = pump();
		Pump second = pump();
		Pump late = pump();
		for (Pump pump : List.of(first, second, late)) {
			price(pump.stationId(), "GAS_92", PRICE);
		}
		sync(first, capture(first, car(company), 20_000));
		sync(second, capture(second, car(company), 20_000)); // 60,000: crosses
		assertThat(alerts(company)).isEqualTo(1L);

		HttpResponse<String> old = sync(late,
				capture(late, car(company), 20_000).at(Instant.now().minus(Duration.ofDays(2))));
		assertThat(results(old)).containsExactly("RECORDED");
		assertThat(alerts(company)).as("still one alert").isEqualTo(1L);
	}

	private static long alerts(String company) {
		return Owner.<Long>queryOne("select count(*) from alerts where type = 'COMPANY_OFFLINE_EXPOSURE' and company_id = ?",
				UUID.fromString(company));
	}

}
