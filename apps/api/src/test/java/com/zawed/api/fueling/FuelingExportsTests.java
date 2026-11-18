package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;

/** Fuelings as CSV: each audience gets exactly its own rows, in a file a spreadsheet reads correctly. */
class FuelingExportsTests extends PosTestSupport {

	private static final LocalDate TODAY = LocalDate.now(ZoneId.of("Africa/Cairo"));

	@Test
	void eachAudienceExportsOnlyItsOwnFuelings() throws Exception {
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String mine = seedCompany().toString();
		String theirs = seedCompany().toString();
		credit(mine, 100_000);
		credit(theirs, 100_000);
		String myRef = JsonPath.read(fuel(pump, car(mine), 20_000).body(), "$.reference");
		String theirRef = JsonPath.read(fuel(pump, car(theirs), 10_000).body(), "$.reference");

		SignedIn accountant = signIn(seed(Role.ACCOUNTANT, new Scope(ScopeType.COMPANY, UUID.fromString(mine)), false));
		HttpResponse<byte[]> file = bytes("/api/v1/company/fuelings/export?from=" + TODAY + "&to=" + TODAY,
				accountant.accessToken());
		assertThat(file.statusCode()).isEqualTo(200);
		assertThat(file.headers().firstValue("Content-Type").orElse("")).startsWith("text/csv");
		assertThat(file.headers().firstValue("Content-Disposition").orElse("")).contains("attachment");
		byte[] body = file.body();
		assertThat(body[0]).as("UTF-8 byte-order mark for Excel").isEqualTo((byte) 0xEF);
		String text = new String(body, StandardCharsets.UTF_8);
		assertThat(text).contains("Reference,Time (Cairo)").contains(myRef).doesNotContain(theirRef);
		assertThat(text).as("20 L at 15.00 = 300.00").contains(",20.00,15.00,300.00,");

		SignedIn station = signIn(seed(Role.STATION_MANAGER,
				new Scope(ScopeType.OPERATOR, UUID.fromString(pump.operatorId())), false));
		String atStation = new String(bytes("/api/v1/station/fuelings/export?from=" + TODAY + "&to=" + TODAY + "&lang=ar",
				station.accessToken()).body(), StandardCharsets.UTF_8);
		assertThat(atStation).contains("المرجع").contains(myRef).contains(theirRef);
	}

	@Test
	void theRangeIsRequiredAndAtMostAYear() throws Exception {
		HttpResponse<String> missing = authed("GET", "/api/v1/ops/fuelings/export", ops.accessToken(), null);
		assertThat(missing.statusCode()).isEqualTo(400);
		HttpResponse<String> tooLong = authed("GET", "/api/v1/ops/fuelings/export?from=" + TODAY.minusYears(2) + "&to=" + TODAY,
				ops.accessToken(), null);
		assertThat(code(tooLong)).isEqualTo("EXPORT_RANGE_TOO_LONG");
	}

	private HttpResponse<byte[]> bytes(String path, String token) throws Exception {
		return java.net.http.HttpClient.newHttpClient()
			.send(request(path).header("Authorization", "Bearer " + token).GET().build(),
					HttpResponse.BodyHandlers.ofByteArray());
	}

	@Test
	void amountsAndVolumesAreWrittenAsPlainDecimals() {
		assertThat(FuelingExports.decimal(31_500, 100)).isEqualTo("315.00");
		assertThat(FuelingExports.decimal(20_000, 1000)).isEqualTo("20.00");
		assertThat(FuelingExports.decimal(-1_550, 100)).isEqualTo("-15.50");
	}

}
