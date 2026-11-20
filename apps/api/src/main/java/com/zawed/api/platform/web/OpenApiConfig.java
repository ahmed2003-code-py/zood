package com.zawed.api.platform.web;

import java.util.List;
import java.util.Map;
import java.util.Set;

import io.swagger.v3.core.converter.ModelConverters;
import io.swagger.v3.oas.models.Components;
import io.swagger.v3.oas.models.OpenAPI;
import io.swagger.v3.oas.models.info.Info;
import io.swagger.v3.oas.models.media.Content;
import io.swagger.v3.oas.models.media.MediaType;
import io.swagger.v3.oas.models.media.Schema;
import io.swagger.v3.oas.models.responses.ApiResponse;
import io.swagger.v3.oas.models.security.SecurityRequirement;
import io.swagger.v3.oas.models.security.SecurityScheme;
import io.swagger.v3.oas.models.servers.Server;

import org.springdoc.core.customizers.OpenApiCustomizer;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

/**
 * The API description the web client's TypeScript types are generated from. The server entry is
 * fixed, so the document is identical wherever it is produced and can be compared with the
 * committed copy in {@code openapi/openapi.json}.
 */
@Configuration(proxyBeanMethods = false)
class OpenApiConfig {

	@Bean
	OpenAPI zawedOpenApi() {
		return new OpenAPI()
			.info(new Info().title("ZAWED API")
				.version("v" + ApiContract.VERSION)
				.description(DESCRIPTION))
			.servers(List.of(new Server().url("/")))
			.components(new Components()
				.addSecuritySchemes(USER_TOKEN, new SecurityScheme().type(SecurityScheme.Type.HTTP)
					.scheme("bearer")
					.bearerFormat("JWT")
					.description("A user's access token: `accessToken` from POST /api/v1/auth/login (or "
							+ "/api/v1/admin/auth/login), after /api/v1/auth/mfa/verify for roles with two-step "
							+ "verification. Valid 15 minutes."))
				.addSecuritySchemes(DEVICE_TOKEN, new SecurityScheme().type(SecurityScheme.Type.HTTP)
					.scheme("bearer")
					.bearerFormat("JWT")
					.description("A POS device token from POST /api/v1/pos/token (signed with the device's own "
							+ "key, ADR 0010). Valid 15 minutes. A user token is never accepted here."))
				.addSecuritySchemes(AGENT_SESSION, new SecurityScheme().type(SecurityScheme.Type.APIKEY)
					.in(SecurityScheme.In.HEADER)
					.name("X-Agent-Session")
					.description("The agent's session from POST /api/v1/pos/agents/login, sent with the device token.")));
	}

	static final String USER_TOKEN = "userToken";
	static final String DEVICE_TOKEN = "deviceToken";
	static final String AGENT_SESSION = "agentSession";

	private static final String DESCRIPTION = """
			Fleet fuel management platform. Money is integer piastres, fuel volume is integer millilitres, \
			timestamps are ISO-8601 UTC. Every error has the ApiError shape, with a stable `code`.

			**Trying it here.** Sign in with POST /api/v1/auth/login (company and station users) or \
			/api/v1/admin/auth/login (Operations), finish two-step verification with /api/v1/auth/mfa/verify \
			where asked, then press **Authorize** and paste the `accessToken` into `userToken`. A POS route \
			needs a device token (`deviceToken`) and, for fuelings, the agent's session (`agentSession`) \
			instead. Lists are paginated with `cursor` and `limit`.""";

	/** Calls that need no credential (they check their own proof, or describe the API). */
	private static final Set<String> PUBLIC = Set.of("/api/v1/health", "/api/v1/health/liveness",
			"/api/v1/health/readiness", "/api/v1/meta", "/api/v1/openapi.json", "/api/v1/receipts/{id}",
			"/api/v1/auth/login", "/api/v1/auth/mfa/verify", "/api/v1/auth/mfa/enroll/start",
			"/api/v1/auth/mfa/enroll/confirm", "/api/v1/auth/refresh", "/api/v1/auth/logout",
			"/api/v1/admin/auth/login", "/api/v1/admin/auth/refresh", "/api/v1/admin/auth/logout",
			"/api/v1/setup/super-admin", "/api/v1/pos/activate", "/api/v1/pos/token", "/api/v1/public/demo-requests");

	/** POS calls made by the device alone, before or without an agent. */
	private static final Set<String> DEVICE_ONLY = Set.of("/api/v1/pos/agents/login", "/api/v1/pos/snapshot",
			"/api/v1/pos/sync");

	/**
	 * Which credential each operation takes, so the reference shows a lock where one is needed and
	 * Authorize sends the right header. Mirrors SecurityConfig; the security chain is what enforces it.
	 */
	@Bean
	OpenApiCustomizer credentials() {
		return (openApi) -> {
			if (openApi.getPaths() == null) {
				return;
			}
			Map<String, List<SecurityRequirement>> byKind = Map.of("user",
					List.of(new SecurityRequirement().addList(USER_TOKEN)), "device",
					List.of(new SecurityRequirement().addList(DEVICE_TOKEN)), "agent",
					List.of(new SecurityRequirement().addList(DEVICE_TOKEN).addList(AGENT_SESSION)));
			openApi.getPaths().forEach((path, item) -> item.readOperations().forEach((operation) -> {
				if (PUBLIC.contains(path)) {
					operation.setSecurity(List.of());
				}
				else if (DEVICE_ONLY.contains(path)) {
					operation.setSecurity(byKind.get("device"));
				}
				else if (path.startsWith("/api/v1/pos/")) {
					operation.setSecurity(byKind.get("agent"));
				}
				else {
					operation.setSecurity(byKind.get("user"));
				}
			}));
		};
	}

	/**
	 * Every operation can fail with the same envelope, so it is declared once here instead of on
	 * each endpoint: the schema is published and attached to every operation as its default response.
	 */
	@Bean
	OpenApiCustomizer errorEnvelope() {
		return (openApi) -> {
			// readAll, not read: it also returns the schemas ApiError refers to (FieldIssue).
			ModelConverters.getInstance(true).readAll(ApiError.class).forEach(openApi.getComponents()::addSchemas);
			ApiResponse failure = new ApiResponse().description("Any refusal or failure")
				.content(new Content().addMediaType("application/json",
						new MediaType().schema(new Schema<>().$ref("#/components/schemas/ApiError"))));
			if (openApi.getPaths() != null) {
				openApi.getPaths()
					.values()
					.forEach((path) -> path.readOperations()
						.forEach((operation) -> operation.getResponses().addApiResponse("default", failure)));
			}
		};
	}

}
