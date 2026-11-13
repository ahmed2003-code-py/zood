package com.zawed.api.ledger;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.net.http.HttpResponse;
import java.sql.Connection;
import java.sql.PreparedStatement;
import java.sql.SQLException;
import java.sql.Statement;
import java.sql.Timestamp;
import java.time.Instant;
import java.util.List;
import java.util.UUID;
import java.util.function.Supplier;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.ledger.Ledger.Account;
import com.zawed.api.ledger.Ledger.Entry;
import com.zawed.api.ledger.Ledger.Posted;
import com.zawed.api.ledger.Ledger.Poster;
import com.zawed.api.ledger.Ledger.Posting;
import com.zawed.api.ledger.Ledger.SourceType;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * The posting engine and the guarantees the database gives it (plan §3.5): parallel credits and
 * debits through real transactions, balance and immutability enforced by PostgreSQL for every role,
 * one posting per source, a wallet that never goes negative, the low-balance crossing.
 */
class LedgerTests extends IdentityTestSupport {

	@Autowired
	Ledger ledger;

	@Autowired
	TransactionTemplate tx;

	// ------------------------------------------------------------------------------- concurrency

	@Test
	void parallelCreditsAndDebitsLeaveTheCachedBalanceEqualToTheLedgerAndNeverNegative() throws Exception {
		UUID company = seedCompany();
		int credits = 10;
		int debits = 10;
		long credit = 1_000;
		long debit = 700;

		List<String> outcomes = Concurrently.run(credits + debits, (i) -> {
			try {
				if (i % 2 == 0) {
					post(SourceType.ADJUSTMENT, UUID.randomUUID(), Entry.debit(Account.platformBank(), credit),
							Entry.credit(Account.companyWallet(company), credit));
					return "CREDIT";
				}
				post(SourceType.ADJUSTMENT, UUID.randomUUID(), Entry.debit(Account.companyWallet(company), debit),
						Entry.credit(Account.platformBank(), debit));
				return "DEBIT";
			}
			catch (ApiException ex) {
				assertThat(ex.code()).isEqualTo("INSUFFICIENT_BALANCE");
				return "REFUSED";
			}
		});

		long creditsPosted = outcomes.stream().filter("CREDIT"::equals).count();
		long debitsPosted = outcomes.stream().filter("DEBIT"::equals).count();
		assertThat(creditsPosted).isEqualTo(credits);
		long expected = creditsPosted * credit - debitsPosted * debit;
		assertThat(expected).isGreaterThanOrEqualTo(0);

		assertThat(cachedBalance(company)).isEqualTo(expected);
		assertThat(ledgerBalance(company)).isEqualTo(expected);

		// The running balance stored on each entry is a consistent chain in insert order.
		List<Object[]> chain = entries(company);
		long running = 0;
		for (Object[] entry : chain) {
			running += "CREDIT".equals(entry[0]) ? (Long) entry[1] : -(Long) entry[1];
			assertThat(running).isGreaterThanOrEqualTo(0);
			assertThat(entry[2]).isEqualTo(running);
		}
		assertThat(running).isEqualTo(expected);
	}

	@Test
	void aDuplicateSourcePostsOnceEvenUnderARace() throws Exception {
		UUID company = seedCompany();
		UUID source = UUID.randomUUID();
		List<String> outcomes = Concurrently.run(2, (i) -> {
			try {
				post(SourceType.ADJUSTMENT, source, Entry.debit(Account.platformBank(), 5_000),
						Entry.credit(Account.companyWallet(company), 5_000));
				return "POSTED";
			}
			catch (ApiException ex) {
				return ex.code();
			}
		});
		assertThat(outcomes).containsExactlyInAnyOrder("POSTED", "ALREADY_POSTED");
		assertThat(cachedBalance(company)).isEqualTo(5_000);
		assertThat(Owner.<Long>queryOne("select count(*) from ledger_transactions where source_id = ?", source))
			.isEqualTo(1L);

		// And again, sequentially: the answer is the same and nothing changes.
		assertThatThrownBy(() -> post(SourceType.ADJUSTMENT, source, Entry.debit(Account.platformBank(), 5_000),
				Entry.credit(Account.companyWallet(company), 5_000)))
			.isInstanceOfSatisfying(ApiException.class, (ex) -> assertThat(ex.code()).isEqualTo("ALREADY_POSTED"));
		assertThat(cachedBalance(company)).isEqualTo(5_000);
	}

	// -------------------------------------------------------------------- the database's own word

	@Test
	void theDatabaseRejectsAnUnbalancedTransactionForEveryRole() throws Exception {
		UUID company = seedCompany();
		for (boolean asApp : new boolean[] { true, false }) {
			try (Connection connection = connect(asApp)) {
				UUID txn = insertTransaction(connection, company);
				insertEntry(connection, txn, bankAccount(), null, "DEBIT", 100, null);
				insertEntry(connection, txn, walletAccount(company), company, "CREDIT", 50, 50L);
				assertThatThrownBy(connection::commit).as(asApp ? "as zawed_app" : "as the owner")
					.hasMessageContaining("does not balance");
			}
			try (Connection connection = connect(asApp)) {
				UUID txn = insertTransaction(connection, company);
				insertEntry(connection, txn, bankAccount(), null, "DEBIT", 100, null);
				assertThatThrownBy(connection::commit).as("one entry").hasMessageContaining("at least two");
			}
			try (Connection connection = connect(asApp)) {
				insertTransaction(connection, company);
				assertThatThrownBy(connection::commit).as("no entries").hasMessageContaining("at least two");
			}
		}
		assertThat(Owner.<Long>queryOne("select count(*) from ledger_transactions where company_id = ?", company))
			.isZero();
	}

	@Test
	void ledgerEntriesAndTransactionsCanNeverBeUpdatedOrDeleted() throws Exception {
		UUID company = seedCompany();
		Posted posted = post(SourceType.ADJUSTMENT, UUID.randomUUID(), Entry.debit(Account.platformBank(), 300),
				Entry.credit(Account.companyWallet(company), 300));
		UUID txn = posted.transactionId();

		try (Connection app = connect(true); Statement sql = app.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("update ledger_entries set amount_piastres = 1 where transaction_id = '" + txn + "'"))
				.hasMessageContaining("permission denied");
		}
		try (Connection app = connect(true); Statement sql = app.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("delete from ledger_entries where transaction_id = '" + txn + "'"))
				.hasMessageContaining("permission denied");
		}
		try (Connection app = connect(true); Statement sql = app.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("delete from ledger_transactions where id = '" + txn + "'"))
				.hasMessageContaining("permission denied");
		}
		// The owner has the grants and is still refused, by the trigger.
		assertThatThrownBy(() -> Owner.update("update ledger_entries set amount_piastres = 1 where transaction_id = ?", txn))
			.hasMessageContaining("immutable");
		assertThatThrownBy(() -> Owner.update("delete from ledger_entries where transaction_id = ?", txn))
			.hasMessageContaining("immutable");
		assertThatThrownBy(() -> Owner.update("update ledger_transactions set reference = 'x' where id = ?", txn))
			.hasMessageContaining("immutable");
		assertThatThrownBy(() -> Owner.update("delete from ledger_transactions where id = ?", txn))
			.hasMessageContaining("immutable");
		assertThatThrownBy(() -> Owner.update("truncate ledger_entries")).hasMessageContaining("immutable");
		assertThat(ledgerBalance(company)).isEqualTo(300);
	}

	@Test
	void aWalletNeverGoesBelowZero() throws Exception {
		UUID company = seedCompany();
		post(SourceType.ADJUSTMENT, UUID.randomUUID(), Entry.debit(Account.platformBank(), 1_000),
				Entry.credit(Account.companyWallet(company), 1_000));
		assertThatThrownBy(() -> post(SourceType.ADJUSTMENT, UUID.randomUUID(),
				Entry.debit(Account.companyWallet(company), 1_001), Entry.credit(Account.platformBank(), 1_001)))
			.isInstanceOfSatisfying(ApiException.class, (ex) -> assertThat(ex.code()).isEqualTo("INSUFFICIENT_BALANCE"));
		assertThat(cachedBalance(company)).isEqualTo(1_000);
		assertThat(ledgerBalance(company)).isEqualTo(1_000);
		// Not even the owner can write a negative cached balance.
		assertThatThrownBy(() -> Owner.update("update company_wallets set balance_piastres = -1 where company_id = ?", company))
			.hasMessageContaining("check");
	}

	@Test
	void anUnbalancedPostingIsRefusedBeforeItReachesTheDatabase() {
		UUID company = seedCompany();
		assertThatThrownBy(() -> new Posting(SourceType.ADJUSTMENT, UUID.randomUUID(), null, null, Poster.SYSTEM,
				List.of(Entry.debit(Account.platformBank(), 100), Entry.credit(Account.companyWallet(company), 99))))
			.isInstanceOf(IllegalArgumentException.class)
			.hasMessageContaining("balance");
		assertThatThrownBy(() -> new Posting(SourceType.ADJUSTMENT, UUID.randomUUID(), null, null, Poster.SYSTEM,
				List.of(Entry.debit(Account.platformBank(), 100))))
			.isInstanceOf(IllegalArgumentException.class);
		assertThatThrownBy(() -> Entry.debit(Account.platformBank(), 0)).isInstanceOf(IllegalArgumentException.class);
	}

	// ------------------------------------------------------------------------------- low balance

	@Test
	void crossingTheThresholdDownwardsIsRecordedOnce() throws Exception {
		UUID company = seedCompany(50_000);
		post(SourceType.ADJUSTMENT, UUID.randomUUID(), Entry.debit(Account.platformBank(), 100_000),
				Entry.credit(Account.companyWallet(company), 100_000));
		assertThat(lowBalanceEvents(company)).isZero();

		Posted crossing = post(SourceType.ADJUSTMENT, UUID.randomUUID(), Entry.debit(Account.companyWallet(company), 60_000),
				Entry.credit(Account.platformBank(), 60_000));
		assertThat(crossing.wallets().getFirst().crossedBelowThreshold()).isTrue();
		assertThat(lowBalanceEvents(company)).isEqualTo(1);

		// Already below: no second record.
		post(SourceType.ADJUSTMENT, UUID.randomUUID(), Entry.debit(Account.companyWallet(company), 10_000),
				Entry.credit(Account.platformBank(), 10_000));
		assertThat(lowBalanceEvents(company)).isEqualTo(1);

		// Back above, then below again: recorded again.
		post(SourceType.ADJUSTMENT, UUID.randomUUID(), Entry.debit(Account.platformBank(), 30_000),
				Entry.credit(Account.companyWallet(company), 30_000));
		post(SourceType.ADJUSTMENT, UUID.randomUUID(), Entry.debit(Account.companyWallet(company), 20_000),
				Entry.credit(Account.platformBank(), 20_000));
		assertThat(lowBalanceEvents(company)).isEqualTo(2);
	}

	// ------------------------------------------------------------------------ wallet provisioning

	@Test
	void aNewCompanyGetsAnEmptyWalletInTheSameTransaction() throws Exception {
		SignedIn ops = signIn(seed(Role.OPS));
		HttpResponse<String> created = authed("POST", "/api/v1/ops/companies", ops.accessToken(),
				"{\"company\":{\"legalName\":\"Wallet Co\",\"nameAr\":\"شركة\",\"nameEn\":\"Wallet Co\"},"
						+ "\"lowBalanceThresholdPiastres\":250000}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		String id = JsonPath.read(created.body(), "$.id");

		HttpResponse<String> wallet = authed("GET", "/api/v1/ops/companies/" + id + "/wallet", ops.accessToken(), null);
		assertThat(wallet.statusCode()).as(wallet.body()).isEqualTo(200);
		assertThat(JsonPath.<Integer>read(wallet.body(), "$.balancePiastres")).isZero();
		assertThat(JsonPath.<Integer>read(wallet.body(), "$.lowBalanceThresholdPiastres")).isEqualTo(250_000);
		assertThat(JsonPath.<Boolean>read(wallet.body(), "$.lowBalance")).isTrue();
		assertThat(Owner.<String>queryOne("select code from ledger_accounts where company_id = ?", UUID.fromString(id)))
			.isEqualTo("company:" + id + ":wallet");
		assertThat(Owner.<Long>queryOne(
				"select count(*) from audit_log where action = 'WALLET_CREATED' and entity_id = ?", UUID.fromString(id)))
			.isEqualTo(1L);
	}

	// ----------------------------------------------------------------------------------- helpers

	/** Posts in its own transaction under a platform scope, as a job or an Ops request would. */
	Posted post(SourceType source, UUID sourceId, Entry... entries) {
		return platform(() -> tx.execute((status) -> ledger
			.post(new Posting(source, sourceId, null, "test posting", Poster.SYSTEM, List.of(entries)))));
	}

	private static <T> T platform(Supplier<T> work) {
		return DbScope.runAs(new DbScope.Context(Scope.PLATFORM, null), work);
	}

	static long cachedBalance(UUID company) {
		return Owner.<Long>queryOne("select balance_piastres from company_wallets where company_id = ?", company);
	}

	static long ledgerBalance(UUID company) {
		// sum() of a bigint is numeric in PostgreSQL; cast back so the driver returns a long.
		return Owner.<Long>queryOne("""
				select coalesce(sum(case direction when 'CREDIT' then amount_piastres else -amount_piastres end), 0)::bigint
				from ledger_entries where company_id = ?
				""", company);
	}

	/** (direction, amount, balance_after) in insert order. */
	private static List<Object[]> entries(UUID company) {
		try (Connection connection = Owner.connect();
				PreparedStatement statement = connection.prepareStatement(
						"select direction, amount_piastres, balance_after_piastres from ledger_entries where company_id = ? order by seq")) {
			statement.setObject(1, company);
			List<Object[]> rows = new java.util.ArrayList<>();
			try (var rs = statement.executeQuery()) {
				while (rs.next()) {
					rows.add(new Object[] { rs.getString(1), rs.getLong(2), rs.getLong(3) });
				}
			}
			return rows;
		}
		catch (SQLException ex) {
			throw new IllegalStateException(ex);
		}
	}

	private static long lowBalanceEvents(UUID company) {
		return Owner.<Long>queryOne(
				"select count(*) from audit_log where action = 'WALLET_LOW_BALANCE' and entity_id = ?", company);
	}

	static UUID bankAccount() {
		return Owner.queryOne("select id from ledger_accounts where code = 'platform:bank'");
	}

	static UUID walletAccount(UUID company) {
		return Owner.queryOne("select account_id from company_wallets where company_id = ?", company);
	}

	/** A raw connection in a transaction with a platform scope, as the runtime role or as the owner. */
	static Connection connect(boolean asApp) throws SQLException {
		Connection connection = Owner.connect();
		connection.setAutoCommit(false);
		try (Statement sql = connection.createStatement()) {
			if (asApp) {
				sql.execute("SET ROLE zawed_app");
			}
			sql.execute("select set_config('app.scope_type', 'PLATFORM', true)");
		}
		return connection;
	}

	static UUID insertTransaction(Connection connection, UUID company) throws SQLException {
		UUID id = UUID.randomUUID();
		try (PreparedStatement statement = connection.prepareStatement("""
				insert into ledger_transactions (id, source_type, source_id, company_id, posted_at)
				values (?, 'ADJUSTMENT', ?, ?, ?)
				""")) {
			statement.setObject(1, id);
			statement.setObject(2, UUID.randomUUID());
			statement.setObject(3, company);
			statement.setTimestamp(4, Timestamp.from(Instant.now()));
			statement.executeUpdate();
		}
		return id;
	}

	static void insertEntry(Connection connection, UUID txn, UUID account, UUID company, String direction, long amount,
			Long balanceAfter) throws SQLException {
		try (PreparedStatement statement = connection.prepareStatement("""
				insert into ledger_entries (id, transaction_id, account_id, company_id, direction, amount_piastres,
				                            balance_after_piastres, posted_at)
				values (?, ?, ?, ?, ?, ?, ?, ?)
				""")) {
			statement.setObject(1, UUID.randomUUID());
			statement.setObject(2, txn);
			statement.setObject(3, account);
			statement.setObject(4, company);
			statement.setString(5, direction);
			statement.setLong(6, amount);
			statement.setObject(7, balanceAfter);
			statement.setTimestamp(8, Timestamp.from(Instant.now()));
			statement.executeUpdate();
		}
	}

}
