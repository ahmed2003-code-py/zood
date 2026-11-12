package com.zawed.api;

import java.io.IOException;
import java.nio.file.Path;

import io.zonky.test.db.postgres.embedded.EmbeddedPostgres;

import org.springframework.boot.SpringApplication;

/**
 * Runs the API on a development machine with its own PostgreSQL, no install and no Docker:
 *
 * <pre>
 * ./mvnw spring-boot:test-run
 * </pre>
 *
 * The database is a real PostgreSQL from the same binaries the tests use. Its data lives in
 * {@code .localdb/} beside this module and survives restarts; delete that folder for a clean
 * database. It lives under {@code src/test} so none of this ships in the production jar.
 */
public final class LocalApiApplication {

	private static final int DATABASE_PORT = 54329;

	private LocalApiApplication() {
	}

	public static void main(String[] args) throws IOException {
		EmbeddedPostgres postgres = EmbeddedPostgres.builder()
			.setPort(DATABASE_PORT)
			.setDataDirectory(Path.of(".localdb").toAbsolutePath())
			.setCleanDataDirectory(false)
			.start();
		Runtime.getRuntime().addShutdownHook(new Thread(() -> {
			try {
				postgres.close();
			}
			catch (IOException ex) {
				// Exiting anyway; PostgreSQL recovers its data directory on the next start.
			}
		}, "embedded-postgres-stop"));

		System.setProperty("spring.datasource.url", postgres.getJdbcUrl("postgres", "postgres"));
		System.setProperty("spring.datasource.username", "postgres");
		System.setProperty("spring.datasource.password", "postgres");
		SpringApplication.run(ZawedApiApplication.class, args);
	}

}
