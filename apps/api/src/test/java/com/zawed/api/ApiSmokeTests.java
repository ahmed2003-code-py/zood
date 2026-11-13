package com.zawed.api;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpRequest;
import java.net.http.HttpResponse;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.support.IntegrationTest;

/** What phase B0 promises: the service starts, reaches its database, and refuses everything else. */
class ApiSmokeTests extends IntegrationTest {

	@Test
	void healthAnswersWithoutDetails() throws Exception {
		HttpResponse<String> response = get("/api/v1/health");

		assertThat(response.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(response.body(), "$.status")).isEqualTo("UP");
		assertThat(response.body()).doesNotContain("components").doesNotContain("postgres");
	}

	@Test
	void livenessAndReadinessAreSeparateProbes() throws Exception {
		assertThat(get("/api/v1/health/liveness").statusCode()).isEqualTo(200);

		HttpResponse<String> readiness = get("/api/v1/health/readiness");
		assertThat(readiness.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(readiness.body(), "$.status")).isEqualTo("UP");
	}

	@Test
	void metaReportsTheServiceAndTheAppliedSchema() throws Exception {
		HttpResponse<String> response = get("/api/v1/meta");

		assertThat(response.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(response.body(), "$.service")).isEqualTo("zawed-api");
		assertThat(JsonPath.<Integer>read(response.body(), "$.contractVersion")).isEqualTo(1);
		assertThat(JsonPath.<String>read(response.body(), "$.environment")).isEqualTo("test");
		// The latest applied migration: a plain number, never a failed or missing one.
		assertThat(JsonPath.<String>read(response.body(), "$.schemaVersion")).matches("[1-9][0-9]*");
		assertThat(JsonPath.<String>read(response.body(), "$.serverTime")).endsWith("Z");
	}

	@Test
	void everythingNotListedAsPublicIsRefusedInTheErrorEnvelope() throws Exception {
		HttpResponse<String> response = get("/api/v1/companies");

		assertThat(response.statusCode()).isEqualTo(401);
		assertThat(response.headers().firstValue("Content-Type")).hasValueSatisfying((type) -> assertThat(type)
			.startsWith("application/json"));
		assertThat(JsonPath.<Integer>read(response.body(), "$.statusCode")).isEqualTo(401);
		assertThat(JsonPath.<String>read(response.body(), "$.code")).isEqualTo("UNAUTHENTICATED");
		assertThat(JsonPath.<Boolean>read(response.body(), "$.retryable")).isFalse();
		assertThat(JsonPath.<String>read(response.body(), "$.requestId"))
			.isEqualTo(response.headers().firstValue("X-Request-Id").orElseThrow());
	}

	@Test
	void aRouteThatDoesNotExistIsRefusedTheSameWay() throws Exception {
		assertThat(get("/api/v1/no-such-thing").statusCode()).isEqualTo(401);
		assertThat(get("/actuator/env").statusCode()).isEqualTo(401);
		assertThat(get("/").statusCode()).isEqualTo(401);
	}

	@Test
	void onlyTheThreeHealthProbesArePublicAndNoOtherManagementEndpointExists() throws Exception {
		for (String path : new String[] { "/api/v1/health/db", "/api/v1/health/anything", "/api/v1/env",
				"/api/v1/heapdump", "/api/v1" }) {
			HttpResponse<String> response = get(path);
			assertThat(response.statusCode()).as(path).isEqualTo(401);
			assertThat(JsonPath.<String>read(response.body(), "$.code")).as(path).isEqualTo("UNAUTHENTICATED");
		}
	}

	@Test
	void aPublicRouteIsPublicForReadingOnly() throws Exception {
		HttpResponse<String> response = send(request("/api/v1/meta").POST(HttpRequest.BodyPublishers.noBody()));

		assertThat(response.statusCode()).isEqualTo(401);
	}

	@Test
	void echoesAPlainInboundRequestId() throws Exception {
		HttpResponse<String> response = send(request("/api/v1/meta").header("X-Request-Id", "web-7f3a.42_b").GET());

		assertThat(response.headers().firstValue("X-Request-Id")).hasValue("web-7f3a.42_b");
	}

	@Test
	void replacesAnInboundRequestIdThatIsNotPlain() throws Exception {
		HttpResponse<String> response = send(
				request("/api/v1/meta").header("X-Request-Id", "bad id; with=separators").GET());

		assertThat(response.headers().firstValue("X-Request-Id")).hasValueSatisfying(
				(id) -> assertThat(id).matches("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"));
	}

	@Test
	void answersAPreflightFromAnAllowedOrigin() throws Exception {
		HttpResponse<String> response = send(request("/api/v1/meta").header("Origin", "http://localhost:5173")
			.header("Access-Control-Request-Method", "GET")
			.method("OPTIONS", HttpRequest.BodyPublishers.noBody()));

		assertThat(response.statusCode()).isEqualTo(200);
		assertThat(response.headers().firstValue("Access-Control-Allow-Origin")).hasValue("http://localhost:5173");
		assertThat(response.headers().firstValue("Access-Control-Allow-Credentials")).hasValue("true");
	}

	@Test
	void refusesAPreflightFromAnyOtherOrigin() throws Exception {
		HttpResponse<String> response = send(request("/api/v1/meta").header("Origin", "https://evil.example")
			.header("Access-Control-Request-Method", "GET")
			.method("OPTIONS", HttpRequest.BodyPublishers.noBody()));

		assertThat(response.statusCode()).isEqualTo(403);
		assertThat(response.headers().firstValue("Access-Control-Allow-Origin")).isEmpty();
	}

}
