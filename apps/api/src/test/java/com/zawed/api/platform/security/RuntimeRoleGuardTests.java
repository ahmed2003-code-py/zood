package com.zawed.api.platform.security;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.util.Properties;

import org.junit.jupiter.api.Test;
import org.springframework.jdbc.datasource.DriverManagerDataSource;
import org.springframework.transaction.CannotCreateTransactionException;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.support.Owner;
import com.zawed.api.support.TestDatabase;

/**
 * Row-level security binds zawed_app and not the owner (V5), so a transaction that is not running
 * as zawed_app must never start: it would see every tenant's rows.
 */
class RuntimeRoleGuardTests {

	@Test
	void aTransactionAsTheOwnerIsRefused() {
		DriverManagerDataSource owner = new DriverManagerDataSource(TestDatabase.jdbcUrl(), TestDatabase.USERNAME,
				TestDatabase.PASSWORD);
		TransactionTemplate transactions = new TransactionTemplate(new ScopedTransactionManager(owner));

		assertThatThrownBy(() -> transactions.execute((status) -> "never runs"))
			.isInstanceOf(CannotCreateTransactionException.class)
			.rootCause()
			.hasMessageContaining("Refusing a transaction as database role 'postgres'");
	}

	@Test
	void aTransactionAsTheRuntimeRoleRuns() {
		DriverManagerDataSource owner = new DriverManagerDataSource(TestDatabase.jdbcUrl(), TestDatabase.USERNAME,
				TestDatabase.PASSWORD);
		// What the pool does for every connection (application.yaml: connection-init-sql).
		Properties properties = new Properties();
		properties.setProperty("options", "-c role=" + ScopedTransactionManager.RUNTIME_ROLE);
		owner.setConnectionProperties(properties);
		ensureRuntimeRoleExists();
		TransactionTemplate transactions = new TransactionTemplate(new ScopedTransactionManager(owner));

		String result = transactions.execute((status) -> "ran");
		assertThat(result).isEqualTo("ran");
	}

	/** The role comes from V3; create it if this test happens to run before any migration. */
	private static void ensureRuntimeRoleExists() {
		Owner.update("""
				DO $$ BEGIN
				  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'zawed_app') THEN CREATE ROLE zawed_app NOLOGIN; END IF;
				END $$""");
	}

}
