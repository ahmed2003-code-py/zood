package com.zawed.api.platform.meta;

import java.time.Clock;
import java.time.Instant;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.beans.factory.ObjectProvider;
import org.springframework.boot.info.BuildProperties;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.platform.config.ZawedProperties;
import com.zawed.api.platform.web.ApiContract;

/**
 * What is running, for clients and for a person checking a deploy. Public and read-only. It reads
 * the applied schema version from the database, so a 200 here also shows the API can reach it.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/meta")
@Tag(name = "platform")
class MetaController {

	private final Clock clock;
	private final JdbcClient jdbc;
	private final ZawedProperties properties;
	private final String version;

	MetaController(Clock clock, JdbcClient jdbc, ZawedProperties properties, ObjectProvider<BuildProperties> build) {
		this.clock = clock;
		this.jdbc = jdbc;
		this.properties = properties;
		BuildProperties buildProperties = build.getIfAvailable();
		this.version = buildProperties != null ? buildProperties.getVersion() : "dev";
	}

	@GetMapping
	@Operation(operationId = "getMeta", summary = "Service name, version, contract version and schema version")
	MetaResponse meta() {
		String schemaVersion = jdbc
			.sql("select version from flyway_schema_history where success order by installed_rank desc limit 1")
			.query(String.class)
			.optional()
			.orElse(null);
		return new MetaResponse("zawed-api", version, ApiContract.VERSION,
				properties.environment().name().toLowerCase(), clock.instant(), schemaVersion);
	}

	record MetaResponse(@Schema(requiredMode = RequiredMode.REQUIRED) String service,
			@Schema(requiredMode = RequiredMode.REQUIRED) String version,
			@Schema(requiredMode = RequiredMode.REQUIRED,
					description = "Increases only on a change an existing client cannot ignore") int contractVersion,
			@Schema(requiredMode = RequiredMode.REQUIRED) String environment,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant serverTime,
			@Schema(description = "Latest applied database migration") String schemaVersion) {
	}

}
