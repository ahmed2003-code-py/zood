package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.sql.Connection;
import java.sql.PreparedStatement;
import java.sql.SQLException;
import java.util.UUID;

import javax.sql.DataSource;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;

/**
 * Isolation at the database layer (plan §3.6, PRD §9): even a hand-written query, run the way the
 * application runs (pooled connection, role zawed_app), sees only its own scope's rows, cannot write
 * into another scope, and cannot change the audit trail.
 */
class RowLevelSecurityTests extends IdentityTestSupport {

	@Autowired
	DataSource dataSource;

	@Test
	void theApplicationNeverRunsAsTheOwner() throws Exception {
		try (Connection connection = dataSource.getConnection(); var rs = connection.createStatement().executeQuery(
				"select current_user, (select rolsuper from pg_roles where rolname = current_user)")) {
			rs.next();
			assertThat(rs.getString(1)).isEqualTo("zawed_app");
			assertThat(rs.getBoolean(2)).isFalse();
		}
	}

	@Test
	void aCompanyScopeSeesOnlyItsOwnUsers() throws Exception {
		Scope companyA = Scope.company(UUID.randomUUID());
		Scope companyB = Scope.company(UUID.randomUUID());
		SeededUser a = seed(Role.FLEET_MANAGER, companyA, false);
		SeededUser b = seed(Role.ACCOUNTANT, companyB, false);

		try (Connection connection = scoped(companyA)) {
			assertThat(count(connection, "select count(*) from users where id = ?", a.id())).isEqualTo(1);
			// Asked for by id, filtered by nothing: still invisible.
			assertThat(count(connection, "select count(*) from users where id = ?", b.id())).isZero();
			assertThat(count(connection, "select count(*) from users where scope_id = ?", companyB.id())).isZero();
			connection.rollback();
		}
	}

	@Test
	void withoutAScopeNoUserIsVisible() throws Exception {
		SeededUser user = seed(Role.FLEET_MANAGER);
		try (Connection connection = dataSource.getConnection()) {
			connection.setAutoCommit(false);
			assertThat(count(connection, "select count(*) from users where id = ?", user.id())).isZero();
			connection.rollback();
		}
	}

	@Test
	void aCompanyScopeCannotMoveAUserIntoAnotherCompany() throws Exception {
		Scope companyA = Scope.company(UUID.randomUUID());
		SeededUser a = seed(Role.FLEET_MANAGER, companyA, false);
		try (Connection connection = scoped(companyA)) {
			assertThatThrownBy(() -> {
				try (PreparedStatement statement = connection
					.prepareStatement("update users set scope_id = ? where id = ?")) {
					statement.setObject(1, UUID.randomUUID());
					statement.setObject(2, a.id());
					statement.executeUpdate();
				}
			}).isInstanceOf(SQLException.class).hasMessageContaining("row-level security");
			connection.rollback();
		}
	}

	@Test
	void theAuditTrailCannotBeChangedOrDeleted() throws Exception {
		seed(Role.FLEET_MANAGER);
		try (Connection connection = scoped(Scope.PLATFORM)) {
			assertThatThrownBy(() -> connection.createStatement().executeUpdate("update audit_log set action = 'X'"))
				.isInstanceOf(SQLException.class)
				.hasMessageContaining("permission denied");
			connection.rollback();
		}
		try (Connection connection = scoped(Scope.PLATFORM)) {
			assertThatThrownBy(() -> connection.createStatement().executeUpdate("delete from audit_log"))
				.isInstanceOf(SQLException.class)
				.hasMessageContaining("permission denied");
			connection.rollback();
		}
	}

	@Test
	void usersCannotBeDeletedOnlyDeactivated() throws Exception {
		try (Connection connection = scoped(Scope.PLATFORM)) {
			assertThatThrownBy(() -> connection.createStatement().executeUpdate("delete from users"))
				.isInstanceOf(SQLException.class)
				.hasMessageContaining("permission denied");
			connection.rollback();
		}
	}

	@Test
	void theScopeDoesNotLeakIntoTheNextTransactionOnThePooledConnection() throws Exception {
		SeededUser user = seed(Role.FLEET_MANAGER);
		try (Connection connection = scoped(Scope.PLATFORM)) {
			assertThat(count(connection, "select count(*) from users where id = ?", user.id())).isEqualTo(1);
			connection.commit();
			// A new transaction on the same connection: the scope was transaction-local.
			assertThat(count(connection, "select count(*) from users where id = ?", user.id())).isZero();
			connection.rollback();
		}
	}

	/** A pooled connection inside a transaction with a scope set, the way ScopedTransactionManager does it. */
	private Connection scoped(Scope scope) throws SQLException {
		Connection connection = dataSource.getConnection();
		connection.setAutoCommit(false);
		try (PreparedStatement statement = connection.prepareStatement(
				"select set_config('app.scope_type', ?, true), set_config('app.scope_id', ?, true)")) {
			statement.setString(1, scope.type().name());
			statement.setString(2, scope.id() == null ? "" : scope.id().toString());
			statement.execute();
		}
		return connection;
	}

	private static long count(Connection connection, String sql, Object param) throws SQLException {
		try (PreparedStatement statement = connection.prepareStatement(sql)) {
			statement.setObject(1, param);
			try (var rs = statement.executeQuery()) {
				rs.next();
				return rs.getLong(1);
			}
		}
	}

}
