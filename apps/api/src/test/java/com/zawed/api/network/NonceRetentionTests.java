package com.zawed.api.network;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.net.http.HttpResponse;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.support.Owner;

/** Device nonces are purged once they are past any replay window, and never sooner (V14, audit F5). */
class NonceRetentionTests extends IdentityTestSupport {

	@Autowired
	NonceRetention retention;

	@Test
	void oldNoncesArePurgedAndRecentOnesKept() throws Exception {
		UUID device = device();
		for (int i = 0; i < 12; i++) {
			Owner.update("insert into device_nonces (device_id, nonce, created_at) values (?, ?, now() - interval '3 hours')",
					device, "old-nonce-000000-" + i);
		}
		Owner.update("insert into device_nonces (device_id, nonce, created_at) values (?, ?, now() - interval '5 minutes')",
				device, "fresh-nonce-00000");

		assertThat(retention.purge()).isGreaterThanOrEqualTo(12);
		assertThat(Owner.<Long>queryOne("select count(*) from device_nonces where device_id = ?", device)).isEqualTo(1L);
		assertThat(Owner.<String>queryOne("select nonce from device_nonces where device_id = ?", device))
			.isEqualTo("fresh-nonce-00000");
	}

	@Test
	void theFunctionRefusesToPurgeInsideTheReplayWindow() {
		assertThatThrownBy(() -> Owner.queryOne("select purge_device_nonces(now() - interval '5 minutes', 10)"))
			.hasMessageContaining("replay window");
	}

	private UUID device() throws Exception {
		SignedIn ops = signIn(seed(Role.OPS));
		String operator = JsonPath.read(authed("POST", "/api/v1/ops/operators", ops.accessToken(),
				"{\"legalName\":\"Op " + UUID.randomUUID() + "\",\"nameAr\":\"م\",\"nameEn\":\"O\"}").body(), "$.id");
		String station = JsonPath.read(authed("POST", "/api/v1/ops/stations", ops.accessToken(),
				"{\"operatorId\":\"" + operator + "\",\"station\":{\"nameAr\":\"م\",\"nameEn\":\"S\",\"connectivity\":\"NORMAL\","
						+ "\"fuelTypes\":[\"GAS_92\"]}}").body(), "$.id");
		HttpResponse<String> registered = authed("POST", "/api/v1/ops/devices", ops.accessToken(),
				"{\"stationId\":\"" + station + "\"}");
		return UUID.fromString(JsonPath.read(registered.body(), "$.device.id"));
	}

}
