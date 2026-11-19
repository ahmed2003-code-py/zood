package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.UUID;

import org.junit.jupiter.api.Test;
import org.springframework.test.context.TestPropertySource;

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

	private static long alerts(String company) {
		return Owner.<Long>queryOne("select count(*) from alerts where type = 'COMPANY_OFFLINE_EXPOSURE' and company_id = ?",
				UUID.fromString(company));
	}

}
