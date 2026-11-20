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
		UUID user = UUID.randomUUID();
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			sql.executeUpdate(claim(pending, companyA, user));
			connection.commit();
		}
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(claim(UUID.randomUUID(), companyB, user)))
				.as("a request for another company").hasMessageContaining("row-level security");
		}
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(topUpInsert(UUID.randomUUID(), companyA, "REJECTED")))
				.as("a request born decided (the V12 guard answers before the policy)").hasMessageContaining("starts pending");
		}
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("""
					update topup_requests set status = 'CONFIRMED', decided_at = now(), ledger_transaction_id = (
					  select id from ledger_transactions limit 1) where id = '%s'
					""".formatted(pending))).as("confirming its own request")
				.hasMessageMatching(
						"(?s).*(row-level security|only a verified top-up is approved|the decider must be the acting user).*");
		}
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			assertThat(sql.executeUpdate("update topup_requests set status = 'CANCELLED', decided_by = '" + user
					+ "', decided_at = now() where id = '" + pending + "'")).isEqualTo(1);
			connection.commit();
		}
	}

	@Test
	void aCompanyRequestCarriesTheClaimAndNothingElse() throws SQLException {
		// Review finding (low, V17): the V12 insert branch let a company session write a PENDING row that
		// already carried a verification, Operations' origin, another receiving account or another
		// requester. None is reachable through the API (TopUps.request writes none of them); the
		// database now refuses them too, whoever writes the SQL.
		UUID user = UUID.randomUUID();
		String verified = "verified_amount_piastres = 100, verified_by = '" + user + "', verified_at = now()";
		java.util.Map<String, String> forged = new java.util.LinkedHashMap<>();
		forged.put(verified, "a request is not verified");
		forged.put("evidence_reference = 'statement'", "a request is not verified");
		forged.put("origin = 'OPS_RECORDED'", "a company request");
		forged.put("receiving_account = 'CIB'", "receiving account");
		forged.put("requested_by = '" + UUID.randomUUID() + "'", "the requester must be the acting user");
		forged.put("requested_by = null", "the requester must be the acting user");
		forged.put("decided_by = '" + user + "'", "carries no decision");
		forged.put("decision_reason = 'pre-decided'", "carries no decision");
		for (var entry : forged.entrySet()) {
			UUID id = UUID.randomUUID();
			try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
				assertThatThrownBy(() -> sql.executeUpdate(claimWith(id, companyA, user, entry.getKey())))
					.as(entry.getKey()).hasMessageContaining(entry.getValue());
			}
		}
		// The honest claim still goes in.
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			assertThat(sql.executeUpdate(claim(UUID.randomUUID(), companyA, user))).isEqualTo(1);
		}
	}

	@Test
	void aWithdrawalChangesNothingButTheStatusAndTheDecision() throws SQLException {
		// Review finding (low): the company's role has UPDATE on the whole table (V6), so the statement
		// that withdraws a request could also rewrite it. Each forged column is refused; the decision
		// columns and updated_at may change.
		UUID user = UUID.randomUUID();
		UUID pending = UUID.randomUUID();
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			sql.executeUpdate(claim(pending, companyA, user));
			connection.commit();
		}
		String withdraw = "update topup_requests set status = 'CANCELLED', decided_by = '" + user
				+ "', decided_at = now(), %s where id = '" + pending + "'";
		for (String forged : List.of("verified_amount_piastres = 100, verified_by = '" + user + "', verified_at = now()",
				"evidence_reference = 'statement line 4'", "receiving_account = 'CIB'",
				"bank_reference_key = 'OTHERKEY" + pending.toString().replace("-", "").toUpperCase() + "'",
				"bank_reference = 'OTHER'", "note = 'rewritten'", "ledger_transaction_id = '" + UUID.randomUUID() + "'",
				"created_at = now() - interval '1 day'")) {
			try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
				assertThatThrownBy(() -> sql.executeUpdate(withdraw.formatted(forged))).as(forged)
					.hasMessageContaining("a withdrawal changes only the status and the decision");
			}
		}
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			assertThat(sql.executeUpdate(withdraw.formatted("decision_reason = 'sent twice', updated_at = now()")))
				.isEqualTo(1);
			connection.commit();
		}
		assertThat(Owner.<String>queryOne(
				"select status || '/' || receiving_account || '/' || coalesce(verified_by::text, '-') from topup_requests where id = ?",
				pending)).isEqualTo("CANCELLED/MAIN/-");
	}

	@Test
	void onlyTheCompanyWithdrawsARequestAndOnlyAsItself() throws SQLException {
		// Review finding (low, V17): the V12 guard did not check who cancels.
		UUID user = UUID.randomUUID();
		UUID pending = UUID.randomUUID();
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			sql.executeUpdate(claim(pending, companyA, user));
			connection.commit();
		}
		String cancelAs = "update topup_requests set status = 'CANCELLED', decided_by = %s, decided_at = now() where id = '"
				+ pending + "'";
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(cancelAs.formatted("'" + UUID.randomUUID() + "'")))
				.as("in someone else's name").hasMessageContaining("the decider must be the acting user");
		}
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(cancelAs.formatted("null"))).as("in nobody's name")
				.hasMessageContaining("the decider must be the acting user");
		}
		UUID opsUser = UUID.randomUUID();
		try (Connection connection = app("PLATFORM", null, opsUser); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(cancelAs.formatted("'" + opsUser + "'")))
				.as("Operations rejects with a reason; it never withdraws a company's request")
				.hasMessageContaining("only the company withdraws");
		}

		// A verified top-up waiting for approval can never be cancelled, by anyone: there is no such
		// path in TopUps (Operations rejects it, with a reason, audited as TOPUP_REJECTED).
		UUID awaiting = UUID.randomUUID();
		Owner.update("alter table topup_requests disable trigger topup_requests_guard");
		try {
			Owner.update("""
					insert into topup_requests (id, company_id, origin, amount_piastres, bank_reference, bank_reference_key,
					                            transfer_date, status, requested_at, verified_amount_piastres, verified_by,
					                            verified_at, created_at, updated_at)
					values (?, ?, 'OPS_RECORDED', 100, 'AWAIT', ?, current_date, 'PENDING_APPROVAL', now(), 100, ?, now(),
					        now(), now())
					""", awaiting, companyA, "AWAIT" + awaiting.toString().replace("-", "").toUpperCase(), UUID.randomUUID());
		}
		finally {
			Owner.update("alter table topup_requests enable trigger topup_requests_guard");
		}
		String cancelAwaiting = "update topup_requests set status = 'CANCELLED', decided_by = '%s', decided_at = now() "
				+ "where id = '" + awaiting + "'";
		try (Connection connection = app("PLATFORM", null, opsUser); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(cancelAwaiting.formatted(opsUser)))
				.hasMessageContaining("only a request nobody has verified is withdrawn");
		}
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			assertThat(sql.executeUpdate(cancelAwaiting.formatted(user))).as("not even visible to the company's update")
				.isZero();
		}

		// The company's own withdrawal, in its own name, still works.
		try (Connection connection = app("COMPANY", companyA, user); Statement sql = connection.createStatement()) {
			assertThat(sql.executeUpdate(cancelAs.formatted("'" + user + "'"))).isEqualTo(1);
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
		// Since V12 no row can be inserted already decided, not even by the owner; the test plants one
		// with the guard switched off, then proves nobody can change it.
		Owner.update("alter table topup_requests disable trigger topup_requests_guard");
		try {
			Owner.update(topUpInsert(decided, companyA, "REJECTED"));
		}
		finally {
			Owner.update("alter table topup_requests enable trigger topup_requests_guard");
		}
		String reopen = "update topup_requests set status = 'CANCELLED', decision_reason = null where id = '%s'"
			.formatted(decided);
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(reopen)).hasMessageContaining("already decided");
		}
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			// Since V12 the company's update policy passes only PENDING rows: the decided row is not there.
			assertThat(sql.executeUpdate(reopen)).isZero();
		}
		assertThatThrownBy(() -> Owner.update(reopen)).as("the owner too").hasMessageContaining("already decided");

		UUID pending = UUID.randomUUID();
		Owner.update(topUpInsert(pending, companyA, "PENDING"));
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(
					"update topup_requests set amount_piastres = 999999 where id = '%s'".formatted(pending)))
				.hasMessageContaining("request facts never change");
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

	/** A company's top-up request exactly as {@code TopUps.request} writes it. */
	private static String claim(UUID id, UUID company, UUID user) {
		return claimWith(id, company, user, null);
	}

	/** The same request with columns forced, as {@code "column = sql value, ..."}. */
	private static String claimWith(UUID id, UUID company, UUID user, String forced) {
		java.util.Map<String, String> columns = new java.util.LinkedHashMap<>();
		columns.put("id", "'" + id + "'");
		columns.put("company_id", "'" + company + "'");
		columns.put("origin", "'COMPANY_REQUEST'");
		columns.put("amount_piastres", "100");
		columns.put("receiving_account", "'MAIN'");
		columns.put("bank_reference", "'REF'");
		columns.put("bank_reference_key", "'ISO" + id.toString().replace("-", "").toUpperCase() + "'");
		columns.put("transfer_date", "current_date");
		columns.put("status", "'PENDING'");
		columns.put("requested_by", "'" + user + "'");
		columns.put("requested_at", "now()");
		columns.put("created_at", "now()");
		columns.put("updated_at", "now()");
		if (forced != null) {
			for (String assignment : forced.split(",")) {
				String[] pair = assignment.split("=", 2);
				columns.put(pair[0].strip(), pair[1].strip());
			}
		}
		return "insert into topup_requests (" + String.join(", ", columns.keySet()) + ") values ("
				+ String.join(", ", columns.values()) + ")";
	}

	private static Connection app(String scopeType, UUID scopeId) throws SQLException {
		return app(scopeType, scopeId, null);
	}

	/** A transaction as the runtime role, told what {@code ScopedTransactionManager} would tell it. */
	private static Connection app(String scopeType, UUID scopeId, UUID userId) throws SQLException {
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
			if (userId != null) {
				sql.execute("select set_config('app.user_id', '" + userId + "', true)");
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
