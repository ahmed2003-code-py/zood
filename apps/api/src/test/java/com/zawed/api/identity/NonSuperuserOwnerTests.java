package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.sql.Timestamp;
import java.time.Instant;
import java.util.UUID;

import org.flywaydb.core.Flyway;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

import com.zawed.api.support.TestDatabase;

/**
 * The migrations and the sign-in path with an ordinary database owner: no superuser, no
 * BYPASSRLS. Railway and the main test database connect as a superuser, which skips row-level
 * security entirely, so only this test shows that the migrations and the SECURITY DEFINER
 * functions work on a managed database where the owner is a plain role (V5).
 */
class NonSuperuserOwnerTests {

	private static final String OWNER = "zawed_owner_test";
	private static final String OWNER_PASSWORD = "owner-test-password";

	private static String url;

	@BeforeAll
	static void migrateAsAnOrdinaryOwner() throws SQLException {
		String main = TestDatabase.jdbcUrl();
		String server = main.substring(0, main.indexOf('/', "jdbc:postgresql://".length()) + 1);
		String database = "owner_" + UUID.randomUUID().toString().replace("-", "");
		try (Connection admin = DriverManager.getConnection(server + "postgres", TestDatabase.USERNAME,
				TestDatabase.PASSWORD); Statement sql = admin.createStatement()) {
			sql.execute("""
					DO $$
					BEGIN
					  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '%1$s') THEN
					    CREATE ROLE %1$s LOGIN PASSWORD '%2$s' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
					  END IF;
					  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'zawed_app') THEN
					    CREATE ROLE zawed_app NOLOGIN;
					  END IF;
					END
					$$""".formatted(OWNER, OWNER_PASSWORD));
			// What a managed database's administrator does once: the runtime role exists and the owner
			// may hand it to itself (V3: GRANT zawed_app TO CURRENT_USER).
			sql.execute("GRANT zawed_app TO " + OWNER + " WITH ADMIN OPTION");
			sql.execute("CREATE DATABASE " + database + " OWNER " + OWNER);
		}
		url = server + database;
		Flyway.configure().dataSource(url, OWNER, OWNER_PASSWORD).load().migrate();
	}

	@Test
	void theRuntimeRoleIsSubjectToRowLevelSecurityOnEveryScopedTable() throws SQLException {
		try (Connection connection = owner()) {
			assertThat(single(connection, "select rolsuper or rolbypassrls from pg_roles where rolname = session_user"))
				.as("the owner is an ordinary role")
				.isEqualTo(false);
			// Without a scope the application's role sees no user, even one that exists.
			bootstrap(connection, "rls-check@zawed.test");
			assertThat(single(connection, "select count(*) from users")).isEqualTo(0L);
			// Row-level security is on for every scoped table, and nothing is left forced on the owner.
			assertThat(single(connection, """
					select count(*) from pg_class
					where relname in ('users', 'audit_log', 'companies', 'departments', 'vehicles', 'rfid_tags',
					                  'limit_rules', 'station_operators', 'stations', 'pos_devices', 'agents',
					                  'fuel_prices')
					  and relrowsecurity and not relforcerowsecurity
					""")).isEqualTo(12L);
		}
	}

	@Test
	void signInFunctionsWorkWithoutAScope() throws SQLException {
		try (Connection connection = owner()) {
			UUID id = bootstrap(connection, "first@zawed.test");

			try (PreparedStatement lookup = connection
				.prepareStatement("select id, app_scope_type() as scope from auth_login_lookup(?)")) {
				lookup.setString(1, "first@zawed.test");
				try (ResultSet rs = lookup.executeQuery()) {
					assertThat(rs.next()).as("the login lookup finds the user").isTrue();
					assertThat(rs.getObject("id", UUID.class)).isEqualTo(id);
				}
			}
			assertThat(single(connection, "select auth_super_admin_exists()")).isEqualTo(true);

			try (PreparedStatement failure = connection.prepareStatement(
					"select auth_record_login_failure(?, true, 'WRONG_PASSWORD', 5, 900, ?, ?, ?, 'req', '127.0.0.0/24')")) {
				failure.setObject(1, id);
				failure.setTimestamp(2, Timestamp.from(Instant.now()));
				failure.setObject(3, UUID.randomUUID());
				failure.setObject(4, UUID.randomUUID());
				failure.execute();
			}
			// The function's platform scope ended with it: the caller still has none.
			assertThat(single(connection, "select coalesce(app_scope_type(), 'none')")).isEqualTo("none");
			assertThat(single(connection, "select count(*) from users")).isEqualTo(0L);

			try (Statement platform = connection.createStatement()) {
				platform.execute("select set_config('app.scope_type', 'PLATFORM', true)");
			}
			assertThat(single(connection, "select failed_login_count from users where email = 'first@zawed.test'"))
				.isEqualTo(1);
			assertThat(single(connection, "select count(*) from audit_log where action = 'LOGIN_FAILED'")).isEqualTo(1L);
		}
	}

	@Test
	void aScopedCallerStillCannotWriteOutsideItsScope() throws SQLException {
		try (Connection connection = owner(); Statement sql = connection.createStatement()) {
			sql.execute("select set_config('app.scope_type', 'COMPANY', true)");
			sql.execute("select set_config('app.scope_id', '" + UUID.randomUUID() + "', true)");
			assertThatThrownBy(() -> sql.execute("""
					insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes,
					                       low_balance_threshold_piastres, created_at, updated_at)
					values ('%s', 'X', 'X', 'X', 'ACTIVE', 180, 0, now(), now())
					""".formatted(UUID.randomUUID()))).hasMessageContaining("row-level security");
		}
	}

	/** A connection as the application uses it: the owner, switched to zawed_app, in a transaction. */
	private static Connection owner() throws SQLException {
		Connection connection = DriverManager.getConnection(url, OWNER, OWNER_PASSWORD);
		connection.setAutoCommit(false);
		try (Statement sql = connection.createStatement()) {
			sql.execute("SET ROLE zawed_app");
		}
		return connection;
	}

	/** Creates the first Super Admin through the bootstrap function, with no scope set. */
	private static UUID bootstrap(Connection connection, String email) throws SQLException {
		UUID id = UUID.randomUUID();
		try (PreparedStatement bootstrap = connection
			.prepareStatement("select auth_bootstrap_super_admin(?, ?, 'First', 'hash', ?, ?, 'req', '127.0.0.0/24')")) {
			bootstrap.setObject(1, id);
			bootstrap.setString(2, email);
			bootstrap.setTimestamp(3, Timestamp.from(Instant.now()));
			bootstrap.setObject(4, UUID.randomUUID());
			try (ResultSet rs = bootstrap.executeQuery()) {
				rs.next();
				assertThat(rs.getBoolean(1)).as("bootstrap created the user").isTrue();
			}
		}
		return id;
	}

	private static Object single(Connection connection, String query) throws SQLException {
		try (Statement sql = connection.createStatement(); ResultSet rs = sql.executeQuery(query)) {
			rs.next();
			return rs.getObject(1);
		}
	}

}
