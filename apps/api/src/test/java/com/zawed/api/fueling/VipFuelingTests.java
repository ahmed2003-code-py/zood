package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;
import com.zawed.api.support.Owner;

/**
 * The VIP flow as modelled (PRD 4.4, 4.5), with its switch on. It ships switched off until the
 * owner decides who pays for VIP fuel; FuelingTests covers the switched-off behaviour.
 */
@TestPropertySource(properties = "zawed.fueling.vip-enabled=true")
class VipFuelingTests extends PosTestSupport {

	@Test
	void aVipFuelingIsRecordedButNotChargedAndItsCeilingFlagsOnlyToOps() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = seedCompany().toString();
		Car vip = car(company, "GAS_92", "VIP", 30_000L, 60_000);

		HttpResponse<String> first = fuel(pump, vip, 25_000);
		assertThat(status(first)).as("VIP skips the wallet: the company has no money").isEqualTo("COMPLETED");
		assertThat(JsonPath.<Boolean>read(first.body(), "$.billable")).isFalse();
		assertThat(walletBalance(company)).isZero();
		assertThat(Owner.<Object>queryOne("select ledger_transaction_id from fuelings where id = ?::uuid",
				JsonPath.<String>read(first.body(), "$.id"))).isNull();

		Owner.update("update rfid_tags set cooldown_until = null where vehicle_id = ?::uuid", vip.vehicleId());
		assertThat(status(fuel(pump, vip, 10_000))).isEqualTo("COMPLETED"); // 35 L > 30 L ceiling

		List<String> types = JsonPath.read(authed("GET", "/api/v1/ops/alerts?companyId=" + company, ops.accessToken(), null)
			.body(), "$.items[*].type");
		assertThat(types).containsExactly("VIP_CEILING_PASSED");
		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, new Scope(ScopeType.COMPANY, UUID.fromString(company)), false));
		List<Object> companyAlerts = JsonPath.read(authed("GET", "/api/v1/company/alerts", manager.accessToken(), null)
			.body(), "$.items");
		assertThat(companyAlerts).as("the ceiling is invisible to the company").isEmpty();
	}

	private static String status(HttpResponse<String> response) {
		assertThat(response.statusCode()).as(response.body()).isEqualTo(201);
		return JsonPath.read(response.body(), "$.status");
	}

	private static long walletBalance(String company) {
		return Owner.<Long>queryOne("select balance_piastres from company_wallets where company_id = ?::uuid", company);
	}

}
