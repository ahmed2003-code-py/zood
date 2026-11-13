package com.zawed.api.ledger;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.sql.Connection;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.util.List;
import java.util.UUID;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.support.IntegrationTest;
import com.zawed.api.support.Owner;

/**
 * Row-level security on the B3 tables, straight against the database as the runtime role: a
 * platform scope sees all, a company sees its own wallet, entries and top-ups and can only ask and
 * cancel, an operator sees none of it, and nobody can touch the ledger or the reconciliation tables.
 */
class LedgerIsolationTests extends IntegrationTest {

	private static final List<String> TABLES = List.of("ledger_accounts", "ledger_transactions", "ledger_entries",
			"company_wallets", "topup_requests", "reconciliation_runs", "reconciliation_findings");

	private static UUID companyA;
	private static UUID companyB;

	@BeforeEach
	void seed() {
		if (companyA != null) {
			return;
		}
		companyA = IdentityTestSupport.seedCompany();
		companyB = IdentityTestSupport.seedCompany();
		credit(companyA, 7_000);
		credit(companyB, 9_000);
		Owner.update("""
				insert into topup_requests (id, company_id, amount_piastres, bank_reference, bank_reference_key, transfer_date,
				                            status, requested_at, created_at, updated_at)
				values (?, ?, 100, 'ISO-B', ?, current_date, 'PENDING', now(), now(), now())
				""", UUID.randomUUID(), companyB, "ISOB" + companyB.toString().replace("-", "").toUpperCase());
	}

	@Test
	void withoutAScopeNothingIsVisible() throws SQLException {
		try (Connection connection = app(null, null)) {
			for (String table : TABLES) {
				assertThat(count(connection, table)).as(table).isZero();
			}
		}
	}

	@Test
	void aCompanySeesItsOwnWalletEntriesAndTopUpsOnly() throws SQLException {
		try (Connection connection = app("COMPANY", companyA)) {
			assertThat(count(connection, "company_wallets")).isEqualTo(1);
			assertThat(count(connection, "company_wallets where company_id = '" + companyA + "'")).isEqualTo(1);
			assertThat(count(connection, "ledger_entries where company_id = '" + companyB + "'")).isZero();
			assertThat(count(connection, "ledger_entries")).isEqualTo(count(connection,
					"ledger_entries where company_id = '" + companyA + "'"));
			assertThat(count(connection, "ledger_entries where company_id = '" + companyA + "'")).isGreaterThan(0);
			// The bank side of its own top-up is not its business, nor is the bank account.
			assertThat(count(connection, "ledger_entries where company_id is null")).isZero();
			assertThat(count(connection, "ledger_accounts where code = 'platform:bank'")).isZero();
			assertThat(count(connection, "ledger_transactions where company_id <> '" + companyA + "'")).isZero();
			assertThat(count(connection, "topup_requests where company_id = '" + companyB + "'")).isZero();
			assertThat(count(connection, "reconciliation_runs")).isZero();
			assertThat(count(connection, "reconciliation_findings")).isZero();
		}
	}

	@Test
	void anOperatorSeesNoneOfIt() throws SQLException {
		try (Connection connection = app("OPERATOR", UUID.randomUUID())) {
			for (String table : TABLES) {
				assertThat(count(connection, table)).as(table).isZero();
			}
		}
	}

	@Test
	void aCompanyCanOnlyAskForATopUpAndWithdrawIt() throws SQLException {
		UUID pending = UUID.randomUUID();
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			sql.executeUpdate(topUpInsert(pending, companyA, "PENDING"));
			connection.commit();
		}
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(topUpInsert(UUID.randomUUID(), companyB, "PENDING")))
				.as("a request for another company").hasMessageContaining("row-level security");
		}
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(topUpInsert(UUID.randomUUID(), companyA, "REJECTED")))
				.as("a request born decided").hasMessageContaining("row-level security");
		}
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("""
					update topup_requests set status = 'CONFIRMED', decided_at = now(), ledger_transaction_id = (
					  select id from ledger_transactions limit 1) where id = '%s'
					""".formatted(pending))).as("confirming its own request").hasMessageContaining("row-level security");
		}
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			assertThat(sql.executeUpdate("update topup_requests set status = 'CANCELLED', decided_at = now() where id = '"
					+ pending + "'")).isEqualTo(1);
			connection.commit();
		}
	}

	@Test
	void aCompanyCannotWriteTheLedgerOrItsWallet() throws SQLException {
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("update company_wallets set balance_piastres = 1000000"))
				.hasMessageContaining("row-level security");
		}
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("""
					insert into ledger_transactions (id, source_type, source_id, company_id, posted_at)
					values ('%s', 'ADJUSTMENT', '%s', '%s', now())
					""".formatted(UUID.randomUUID(), UUID.randomUUID(), companyA))).hasMessageContaining("row-level security");
		}
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("""
					insert into ledger_entries (id, transaction_id, account_id, company_id, direction, amount_piastres, posted_at)
					select '%s', t.id, w.account_id, '%s', 'CREDIT', 1, now()
					from ledger_transactions t, company_wallets w limit 1
					""".formatted(UUID.randomUUID(), companyA))).hasMessageContaining("row-level security");
		}
	}

	@Test
	void nothingCanBeDeletedAndTheReconciliationTablesAreReadOnly() throws SQLException {
		for (String table : TABLES) {
			try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
				assertThatThrownBy(() -> sql.executeUpdate("delete from " + table)).as("delete " + table)
					.hasMessageContaining("permission denied");
			}
		}
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("""
					insert into reconciliation_runs (id, trigger, started_at) values ('%s', 'MANUAL', now())
					""".formatted(UUID.randomUUID()))).hasMessageContaining("permission denied");
		}
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("update ledger_transactions set reference = 'x'"))
				.hasMessageContaining("permission denied");
		}
	}

	@Test
	void aDecidedTopUpNeverChangesForAnyRole() throws SQLException {
		// B3 review finding 4: reopening a decided request would free its reference to credit again.
		UUID decided = UUID.randomUUID();
		Owner.update(topUpInsert(decided, companyA, "REJECTED"));
		String reopen = "update topup_requests set status = 'CANCELLED', decision_reason = null where id = '%s'"
			.formatted(decided);
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(reopen)).hasMessageContaining("can no longer change");
		}
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(reopen)).hasMessageContaining("can no longer change");
		}
		assertThatThrownBy(() -> Owner.update(reopen)).as("the owner too").hasMessageContaining("can no longer change");

		UUID pending = UUID.randomUUID();
		Owner.update(topUpInsert(pending, companyA, "PENDING"));
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(
					"update topup_requests set amount_piastres = 999999 where id = '%s'".formatted(pending)))
				.hasMessageContaining("cannot change its company, amount");
		}
	}

	@Test
	void onlyAWalletsCachedBalanceCanBeUpdated() throws SQLException {
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(
					"update company_wallets set account_id = account_id where company_id = '%s'".formatted(companyA)))
				.hasMessageContaining("permission denied");
		}
	}

	@Test
	void aReferenceKeyOutsideTheCanonicalShapeIsRefused() {
		assertThatThrownBy(() -> Owner.update("""
				insert into topup_requests (id, company_id, amount_piastres, bank_reference, bank_reference_key, transfer_date,
				                            status, requested_at, created_at, updated_at)
				values (?, ?, 100, 'ft-1', 'FT-1', current_date, 'PENDING', now(), now(), now())
				""", UUID.randomUUID(), companyA)).hasMessageContaining("topup_requests_reference_key_shape");
	}

	// ----------------------------------------------------------------------------------- helpers

	/** A balanced top-up posted as the owner, with the cached balance moved alongside. */
	private static void credit(UUID company, long piastres) {
		UUID txn = UUID.randomUUID();
		UUID bank = Owner.queryOne("select id from ledger_accounts where code = 'platform:bank'");
		UUID wallet = Owner.queryOne("select account_id from company_wallets where company_id = ?", company);
		try (Connection connection = Owner.connect()) {
			connection.setAutoCommit(false);
			try (Statement sql = connection.createStatement()) {
				sql.executeUpdate("""
						insert into ledger_transactions (id, source_type, source_id, company_id, posted_at)
						values ('%s', 'TOPUP', '%s', '%s', now())
						""".formatted(txn, UUID.randomUUID(), company));
				sql.executeUpdate("""
						insert into ledger_entries (id, transaction_id, account_id, company_id, direction, amount_piastres,
						                            balance_after_piastres, posted_at)
						values ('%s', '%s', '%s', null, 'DEBIT', %d, null, now()),
						       ('%s', '%s', '%s', '%s', 'CREDIT', %d, %d, now())
						""".formatted(UUID.randomUUID(), txn, bank, piastres, UUID.randomUUID(), txn, wallet, company, piastres,
						piastres));
				sql.executeUpdate("update company_wallets set balance_piastres = %d where company_id = '%s'".formatted(piastres,
						company));
			}
			connection.commit();
		}
		catch (SQLException ex) {
			throw new IllegalStateException(ex);
		}
	}

	private static String topUpInsert(UUID id, UUID company, String status) {
		String decided = "PENDING".equals(status) ? "null" : "now()";
		return """
				insert into topup_requests (id, company_id, amount_piastres, bank_reference, bank_reference_key, transfer_date,
				                            status, requested_at, decided_at, decision_reason, created_at, updated_at)
				values ('%s', '%s', 100, 'REF', '%s', current_date, '%s', now(), %s, 'r', now(), now())
				""".formatted(id, company, "ISO" + id.toString().replace("-", "").toUpperCase(), status, decided);
	}

	private static Connection app(String scopeType, UUID scopeId) throws SQLException {
		Connection connection = Owner.connect();
		connection.setAutoCommit(false);
		try (Statement sql = connection.createStatement()) {
			sql.execute("SET ROLE zawed_app");
			if (scopeType != null) {
				sql.execute("select set_config('app.scope_type', '" + scopeType + "', true)");
			}
			if (scopeId != null) {
				sql.execute("select set_config('app.scope_id', '" + scopeId + "', true)");
			}
		}
		return connection;
	}

	private static long count(Connection connection, String from) throws SQLException {
		try (Statement sql = connection.createStatement(); ResultSet rs = sql.executeQuery("select count(*) from " + from)) {
			rs.next();
			return rs.getLong(1);
		}
	}

}
