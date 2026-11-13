package com.zawed.api.pricing;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Owner;

/** Effective-dated prices: a price is never backdated, and the latest to take effect wins. */
class PricesTests extends IdentityTestSupport {

	@Autowired
	PriceBook prices;

	private SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	@Test
	void aPriceCannotTakeEffectInThePast() throws Exception {
		HttpResponse<String> response = schedule("GAS_95", null, 2000, Instant.now().minus(1, ChronoUnit.HOURS));
		assertThat(response.statusCode()).isEqualTo(400);
		assertThat(code(response)).isEqualTo("PRICE_BACKDATED");

		// Inside the clock tolerance it is accepted, but stored as taking effect now, not earlier.
		Instant requested = Instant.now().minus(30, ChronoUnit.SECONDS);
		HttpResponse<String> accepted = schedule("GAS_95", null, 2000, requested);
		assertThat(accepted.statusCode()).isEqualTo(201);
		assertThat(Instant.parse(JsonPath.read(accepted.body(), "$.effectiveFrom"))).isAfter(requested.plusSeconds(25));
	}

	@Test
	void theLatestPriceToTakeEffectWinsBetweenNationalAndStation() throws Exception {
		// CNG is used by this test alone, so other tests' prices do not interfere.
		UUID station = seedStation();
		Instant base = Instant.now().plus(10, ChronoUnit.DAYS).truncatedTo(ChronoUnit.SECONDS);
		assertThat(schedule("CNG", null, 700, base).statusCode()).isEqualTo(201);
		assertThat(schedule("CNG", station, 750, base.plus(1, ChronoUnit.DAYS)).statusCode()).isEqualTo(201);
		assertThat(schedule("CNG", null, 800, base.plus(2, ChronoUnit.DAYS)).statusCode()).isEqualTo(201);

		assertThat(priceAt(station, base.plus(12, ChronoUnit.HOURS))).isEqualTo(700);
		assertThat(priceAt(station, base.plus(36, ChronoUnit.HOURS))).isEqualTo(750);
		// A national change after the override supersedes it.
		assertThat(priceAt(station, base.plus(60, ChronoUnit.HOURS))).isEqualTo(800);
		// Another station never saw the override.
		assertThat(priceAt(UUID.randomUUID(), base.plus(36, ChronoUnit.HOURS))).isEqualTo(700);

		HttpResponse<String> same = schedule("CNG", null, 900, base);
		assertThat(same.statusCode()).isEqualTo(409);
		assertThat(code(same)).isEqualTo("PRICE_EXISTS");
	}

	@Test
	void schedulingIsAuditedAndPricesAreNeverEdited() throws Exception {
		HttpResponse<String> created = schedule("GAS_80", null, 1300, Instant.now().plus(400, ChronoUnit.DAYS));
		assertThat(code(created)).isEqualTo("PRICE_TOO_FAR_AHEAD");
		created = schedule("GAS_80", null, 1300, Instant.now().plus(5, ChronoUnit.DAYS));
		assertThat(created.statusCode()).isEqualTo(201);
		UUID id = UUID.fromString(JsonPath.read(created.body(), "$.id"));
		assertThat(Owner.<Long>queryOne("select count(*) from audit_log where action = 'PRICE_SCHEDULED' and entity_id = ?",
				id)).isEqualTo(1L);
		assertThat(Owner.<Boolean>queryOne(
				"select has_table_privilege('zawed_app', 'fuel_prices', 'UPDATE') or has_table_privilege('zawed_app', 'fuel_prices', 'DELETE')"))
			.isFalse();
	}

	private long priceAt(UUID station, Instant at) {
		return DbScope.runAs(new DbScope.Context(Scope.PLATFORM, null), () -> prices.priceAt(station, FuelType.CNG, at).orElseThrow())
			.pricePiastresPerLitre();
	}

	private HttpResponse<String> schedule(String fuel, UUID station, long price, Instant from) throws Exception {
		return authed("POST", "/api/v1/ops/prices", ops.accessToken(),
				"{\"fuelType\":\"" + fuel + "\"," + (station == null ? "" : "\"stationId\":\"" + station + "\",")
						+ "\"pricePiastres\":" + price + ",\"effectiveFrom\":\"" + from + "\"}");
	}

	private static UUID seedStation() {
		UUID operator = UUID.randomUUID();
		UUID station = UUID.randomUUID();
		Owner.update("""
				insert into station_operators (id, legal_name, name_ar, name_en, status, created_at, updated_at)
				values (?, 'Op', 'مشغل', 'Op', 'ACTIVE', now(), now())
				""", operator);
		Owner.update("""
				insert into stations (id, operator_id, name_ar, name_en, time_zone, connectivity, fuel_types, status,
				                      created_at, updated_at)
				values (?, ?, 'محطة', 'Station', 'Africa/Cairo', 'NORMAL', '{CNG}', 'ACTIVE', now(), now())
				""", station, operator);
		return station;
	}

}
