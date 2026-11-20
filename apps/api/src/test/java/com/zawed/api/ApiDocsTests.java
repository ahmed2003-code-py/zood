package com.zawed.api;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.List;
import java.util.Map;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.support.IntegrationTest;

/**
 * The interactive API reference (Swagger UI): it opens without signing in, and it tells a reader
 * which credential each endpoint takes, matching the security chain.
 */
class ApiDocsTests extends IntegrationTest {

	@Test
	void theReferenceOpensWithoutSigningIn() throws Exception {
		HttpResponse<String> docs = get("/api/v1/docs");
		assertThat(docs.statusCode()).isEqualTo(302);
		assertThat(docs.headers().firstValue("Location").orElse("")).endsWith("/api/v1/swagger-ui/index.html");
		assertThat(get("/api/v1/swagger-ui/index.html").statusCode()).isEqualTo(200);
		assertThat(get("/api/v1/openapi.json/swagger-config").statusCode()).isEqualTo(200);
	}

	@Test
	void eachEndpointNamesTheCredentialItTakes() throws Exception {
		String spec = get("/api/v1/openapi.json").body();
		assertThat(JsonPath.<Map<String, Object>>read(spec, "$.components.securitySchemes")).containsKeys("userToken",
				"deviceToken", "agentSession");
		assertThat(JsonPath.<List<Object>>read(spec, "$.paths['/api/v1/auth/login'].post.security")).isEmpty();
		assertThat(JsonPath.<List<Object>>read(spec, "$.paths['/api/v1/public/demo-requests'].post.security")).isEmpty();
		assertThat(JsonPath.<List<Map<String, Object>>>read(spec, "$.paths['/api/v1/ops/leads'].get.security"))
			.containsExactly(Map.of("userToken", List.of()));
		assertThat(JsonPath.<List<Map<String, Object>>>read(spec, "$.paths['/api/v1/ops/fuelings'].get.security"))
			.containsExactly(Map.of("userToken", List.of()));
		assertThat(JsonPath.<List<Map<String, Object>>>read(spec, "$.paths['/api/v1/pos/sync'].post.security"))
			.containsExactly(Map.of("deviceToken", List.of()));
		assertThat(JsonPath.<List<Map<String, Object>>>read(spec, "$.paths['/api/v1/pos/fuelings'].post.security"))
			.containsExactly(Map.of("deviceToken", List.of(), "agentSession", List.of()));
	}

}
