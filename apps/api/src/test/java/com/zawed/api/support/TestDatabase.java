package com.zawed.api.support;

import java.io.IOException;
import java.io.UncheckedIOException;

import io.zonky.test.db.postgres.embedded.EmbeddedPostgres;

/**
 * One real PostgreSQL for the whole test run, started on first use and stopped when the JVM exits.
 * It is a real server from the same binaries on every machine and in CI, so row locks, constraints
 * and (later) row-level security behave as they do in production. No Docker is involved (ADR 13).
 */
public final class TestDatabase {

	public static final String USERNAME = "postgres";
	public static final String PASSWORD = "postgres";
	private static final String DATABASE = "postgres";

	private static EmbeddedPostgres instance;

	private TestDatabase() {
	}

	public static synchronized String jdbcUrl() {
		if (instance == null) {
			try {
				instance = EmbeddedPostgres.builder().start();
			}
			catch (IOException ex) {
				throw new UncheckedIOException("Could not start the embedded PostgreSQL", ex);
			}
			Runtime.getRuntime().addShutdownHook(new Thread(TestDatabase::stop, "embedded-postgres-stop"));
		}
		return instance.getJdbcUrl(USERNAME, DATABASE);
	}

	private static void stop() {
		try {
			instance.close();
		}
		catch (IOException ex) {
			// The JVM is exiting; the temporary data directory is removed with it.
		}
	}

}
