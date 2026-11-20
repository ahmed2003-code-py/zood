package com.zawed.api.support;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.SQLException;
import java.sql.Statement;
import java.util.UUID;

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

	/**
	 * A new, empty database on the same server, for tests that must not share the main one (for example
	 * because they start the application with other keys, which data_key_state records). @return its URL
	 */
	public static String newDatabase(String prefix) {
		String base = jdbcUrl();
		String name = prefix + "_" + UUID.randomUUID().toString().replace("-", "").substring(0, 12);
		try (Connection c = DriverManager.getConnection(base, USERNAME, PASSWORD); Statement sql = c.createStatement()) {
			sql.execute("create database " + name);
		}
		catch (SQLException ex) {
			throw new IllegalStateException(ex);
		}
		int slash = base.lastIndexOf('/');
		int query = base.indexOf('?', slash);
		return base.substring(0, slash + 1) + name + (query < 0 ? "" : base.substring(query));
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
