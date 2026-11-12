package com.zawed.api.platform.web;

import java.util.List;

import io.swagger.v3.core.converter.ModelConverters;
import io.swagger.v3.oas.models.OpenAPI;
import io.swagger.v3.oas.models.info.Info;
import io.swagger.v3.oas.models.media.Content;
import io.swagger.v3.oas.models.media.MediaType;
import io.swagger.v3.oas.models.media.Schema;
import io.swagger.v3.oas.models.responses.ApiResponse;
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
				.description("Fleet fuel management platform. Money is integer piastres, fuel volume is integer "
						+ "millilitres, timestamps are ISO-8601 UTC. Every error has the ApiError shape."))
			.servers(List.of(new Server().url("/")));
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
