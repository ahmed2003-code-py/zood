package com.zawed.api.platform.security;

import java.sql.Connection;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;

import javax.sql.DataSource;

import org.springframework.jdbc.support.JdbcTransactionManager;
import org.springframework.transaction.TransactionDefinition;

/**
 * Tells PostgreSQL, at the start of every transaction, whose data it is working on.
 *
 * <p>The three settings are transaction-local ({@code set_config(..., true)}): they vanish at commit
 * or rollback, so a pooled connection never carries one request's scope into the next. This is the
 * single place the scope is set. Repositories never set it, and a query that runs outside a
 * transaction sees no scoped rows at all, which fails safe.
 */
public class ScopedTransactionManager extends JdbcTransactionManager {

	/** The only role row-level security binds (V3, V5). Every pooled connection switches to it. */
	public static final String RUNTIME_ROLE = "zawed_app";

	private static final String SET_SCOPE = "select set_config('app.scope_type', ?, true), "
			+ "set_config('app.scope_id', ?, true), set_config('app.user_id', ?, true), current_user";

	public ScopedTransactionManager(DataSource dataSource) {
		super(dataSource);
	}

	/**
	 * Sets the scope and, in the same round trip, checks who the connection is. Row-level security
	 * binds {@value #RUNTIME_ROLE} and not the owner (V5), so a connection that somehow still runs as
	 * the owner (a pooler that resets roles, a stray {@code RESET ROLE}, a second data source without
	 * the role switch) would see every tenant's rows. Such a transaction is refused instead.
	 */
	@Override
	protected void prepareTransactionalConnection(Connection connection, TransactionDefinition definition)
			throws SQLException {
		super.prepareTransactionalConnection(connection, definition);
		DbScope.Context context = DbScope.current();
		try (PreparedStatement statement = connection.prepareStatement(SET_SCOPE)) {
			statement.setString(1, context == null ? "" : context.scope().type().name());
			statement.setString(2, context == null || context.scope().id() == null ? "" : context.scope().id().toString());
			statement.setString(3, context == null || context.userId() == null ? "" : context.userId().toString());
			try (ResultSet result = statement.executeQuery()) {
				String role = result.next() ? result.getString(4) : null;
				if (!RUNTIME_ROLE.equals(role)) {
					throw new IllegalStateException("Refusing a transaction as database role '" + role + "': row-level "
							+ "security binds only " + RUNTIME_ROLE + " (check the connection-init-sql SET ROLE)");
				}
			}
		}
	}

}
