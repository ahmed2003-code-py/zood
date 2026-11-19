package com.zawed.api.tenancy;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * Onboarding a company is idempotent with an Idempotency-Key (V13, ADR 0022): a retry, a double click
 * or two racing requests give one company and one wallet; the same key with another body is refused.
 */
class CompanyIdempotencyTests extends IdentityTestSupport {

	private SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	@Test
	void aRetryWithTheSameKeyAnswersTheSameCompany() throws Exception {
		String key = "co-" + UUID.randomUUID();
		String legal = "Idem " + UUID.randomUUID();
		HttpResponse<String> first = create(key, legal);
		assertThat(first.statusCode()).as(first.body()).isEqualTo(201);
		HttpResponse<String> retry = create(key, legal);
		assertThat(retry.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(retry.body(), "$.id")).isEqualTo(JsonPath.<String>read(first.body(), "$.id"));
		assertThat(Owner.<Long>queryOne("select count(*) from companies where legal_name = ?", legal)).isEqualTo(1L);

		HttpResponse<String> different = create(key, legal + " B");
		assertThat(different.statusCode()).isEqualTo(409);
		assertThat(code(different)).isEqualTo("IDEMPOTENCY_KEY_REUSED");
	}

	@Test
	void racingRequestsWithOneKeyCreateOneCompanyAndOneWallet() throws Exception {
		String key = "co-" + UUID.randomUUID();
		String legal = "Race " + UUID.randomUUID();
		List<Integer> statuses = Concurrently.run(4, (i) -> create(key, legal).statusCode());
		assertThat(statuses).containsOnlyOnce(201).allMatch((s) -> s == 201 || s == 200);
		assertThat(Owner.<Long>queryOne("select count(*) from companies where legal_name = ?", legal)).isEqualTo(1L);
		assertThat(Owner.<Long>queryOne(
				"select count(*) from company_wallets w join companies c on c.id = w.company_id where c.legal_name = ?", legal))
			.isEqualTo(1L);
	}

	@Test
	void anotherUsersKeyIsNeitherSeenNorReused() throws Exception {
		String key = "co-" + UUID.randomUUID();
		String legal = "Own " + UUID.randomUUID();
		assertThat(create(key, legal).statusCode()).isEqualTo(201);
		SignedIn colleague = signIn(seed(Role.OPS));
		HttpResponse<String> theirs = send(request("/api/v1/ops/companies").header("Authorization", "Bearer " + colleague.accessToken())
			.header("Content-Type", "application/json")
			.header("Idempotency-Key", key)
			.POST(HttpRequest.BodyPublishers.ofString(body(legal + " other"))));
		assertThat(theirs.statusCode()).as("keys are per user").isEqualTo(201);
	}

	private HttpResponse<String> create(String key, String legal) throws Exception {
		return send(request("/api/v1/ops/companies").header("Authorization", "Bearer " + ops.accessToken())
			.header("Content-Type", "application/json")
			.header("Idempotency-Key", key)
			.POST(HttpRequest.BodyPublishers.ofString(body(legal))));
	}

	private static String body(String legal) {
		return "{\"company\":{\"legalName\":\"" + legal + "\",\"nameAr\":\"شركة\",\"nameEn\":\"Company\"},"
				+ "\"lowBalanceThresholdPiastres\":0}";
	}

}
