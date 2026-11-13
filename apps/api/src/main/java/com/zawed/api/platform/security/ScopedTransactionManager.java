package com.zawed.api.platform.security;

import java.sql.Connection;
import java.sql.PreparedStatement;
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

	private static final String SET_SCOPE = "select set_config('app.scope_type', ?, true), "
			+ "set_config('app.scope_id', ?, true), set_config('app.user_id', ?, true)";

	public ScopedTransactionManager(DataSource dataSource) {
		super(dataSource);
	}

	@Override
	protected void prepareTransactionalConnection(Connection connection, TransactionDefinition definition)
			throws SQLException {
		super.prepareTransactionalConnection(connection, definition);
		DbScope.Context context = DbScope.current();
		try (PreparedStatement statement = connection.prepareStatement(SET_SCOPE)) {
			statement.setString(1, context == null ? "" : context.scope().type().name());
			statement.setString(2, context == null || context.scope().id() == null ? "" : context.scope().id().toString());
			statement.setString(3, context == null || context.userId() == null ? "" : context.userId().toString());
			statement.execute();
		}
	}

}
