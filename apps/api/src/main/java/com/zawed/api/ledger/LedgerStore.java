package com.zawed.api.ledger;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.UUID;

import org.springframework.context.ApplicationEventPublisher;
import org.springframework.dao.DataIntegrityViolationException;
import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Propagation;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.tenancy.CompanyDirectory;

/**
 * The posting engine. Explicit SQL and explicit locks (ADR 0004): every step that touches money is
 * visible here, and the database checks the result again at commit (V6 triggers and constraints).
 */
@Service
class LedgerStore implements Ledger {

	static final String ALREADY_POSTED = "ALREADY_POSTED";
	static final String INSUFFICIENT_BALANCE = "INSUFFICIENT_BALANCE";

	private final JdbcClient jdbc;
	private final IdGenerator ids;
	private final Clock clock;
	private final AuditLog audit;
	private final CompanyDirectory companies;
	private final ApplicationEventPublisher events;

	LedgerStore(JdbcClient jdbc, IdGenerator ids, Clock clock, AuditLog audit, CompanyDirectory companies,
			ApplicationEventPublisher events) {
		this.jdbc = jdbc;
		this.ids = ids;
		this.clock = clock;
		this.audit = audit;
		this.companies = companies;
		this.events = events;
	}

	@Override
	@Transactional(propagation = Propagation.MANDATORY)
	public Wallet lockWallet(UUID companyId) {
		return jdbc.sql("""
				select company_id, account_id, balance_piastres, updated_at from company_wallets
				where company_id = :company for update
				""")
			.param("company", companyId)
			.query(LedgerStore::mapWallet)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Wallet"));
	}

	@Override
	@Transactional(propagation = Propagation.MANDATORY)
	public Posted post(Posting posting) {
		Instant now = clock.instant();

		// 1. Lock every wallet the posting touches, always in ascending company id, so two postings
		//    that touch the same wallets can never wait on each other in a cycle.
		TreeMap<UUID, Wallet> wallets = new TreeMap<>();
		for (Entry entry : posting.entries()) {
			if (entry.account().kind() == Account.Kind.COMPANY_WALLET) {
				wallets.putIfAbsent(entry.account().ownerId(), null);
			}
		}
		for (UUID companyId : wallets.keySet()) {
			wallets.put(companyId, lockWallet(companyId));
		}

		// 2. A source posts once. Checked here, after the locks, so a concurrent duplicate (which
		//    waited on the same wallet lock) sees the first posting and is refused without aborting
		//    the caller's database transaction; the unique index below is the final guard.
		if (sourcePosted(posting.sourceType(), posting.sourceId())) {
			throw alreadyPosted();
		}

		// 3. Accounts, then the transaction row.
		Map<Account, UUID> accountIds = new HashMap<>();
		for (Entry entry : posting.entries()) {
			accountIds.computeIfAbsent(entry.account(), (account) -> accountId(account, wallets, now));
		}
		UUID transactionId = ids.next();
		UUID companyId = wallets.size() == 1 ? wallets.firstKey() : null;
		try {
			jdbc.sql("""
					insert into ledger_transactions (id, source_type, source_id, company_id, reference, description,
					                                 posted_at, posted_by)
					values (:id, :sourceType, :sourceId, :company, :reference, :description, :now, :actor)
					""")
				.param("id", transactionId)
				.param("sourceType", posting.sourceType().name())
				.param("sourceId", posting.sourceId())
				.param("company", companyId)
				.param("reference", posting.reference())
				.param("description", posting.description())
				.param("now", ts(now))
				.param("actor", posting.poster().userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw alreadyPosted();
		}

		// 4. The entries, in order. A wallet entry moves the cached balance under the lock taken in
		//    step 1 and records the balance it leaves behind.
		Map<UUID, Long> balances = new HashMap<>();
		wallets.forEach((id, wallet) -> balances.put(id, wallet.balancePiastres()));
		for (Entry entry : posting.entries()) {
			Long balanceAfter = null;
			UUID entryCompany = null;
			if (entry.account().kind() == Account.Kind.COMPANY_WALLET) {
				entryCompany = entry.account().ownerId();
				long before = balances.get(entryCompany);
				long after = entry.direction() == Direction.CREDIT ? Math.addExact(before, entry.piastres())
						: Math.subtractExact(before, entry.piastres());
				if (after < 0) {
					throw new ApiException(HttpStatus.CONFLICT, INSUFFICIENT_BALANCE, "The wallet balance is not enough",
							null, Map.of("balancePiastres", before, "requiredPiastres", entry.piastres()));
				}
				updateBalance(entryCompany, after, now);
				balances.put(entryCompany, after);
				balanceAfter = after;
			}
			jdbc.sql("""
					insert into ledger_entries (id, transaction_id, account_id, company_id, direction, amount_piastres,
					                            balance_after_piastres, posted_at)
					values (:id, :txn, :account, :company, :direction, :amount, :after, :now)
					""")
				.param("id", ids.next())
				.param("txn", transactionId)
				.param("account", accountIds.get(entry.account()))
				.param("company", entryCompany)
				.param("direction", entry.direction().name())
				.param("amount", entry.piastres())
				.param("after", balanceAfter)
				.param("now", ts(now))
				.update();
		}

		// 5. Low balance (PRD 4.7): recorded once, when a posting crosses the threshold downwards.
		List<WalletChange> changes = new ArrayList<>();
		for (Map.Entry<UUID, Wallet> wallet : wallets.entrySet()) {
			long threshold = companies.find(wallet.getKey())
				.orElseThrow(() -> new IllegalStateException("Wallet " + wallet.getKey() + " has no visible company"))
				.lowBalanceThresholdPiastres();
			WalletChange change = new WalletChange(wallet.getKey(), wallet.getValue().balancePiastres(),
					balances.get(wallet.getKey()), threshold);
			changes.add(change);
			if (change.crossedBelowThreshold()) {
				audit.record(new AuditLog.Actor(posting.poster().userId(), posting.poster().role()), "WALLET_LOW_BALANCE",
						"COMPANY_WALLET", change.companyId(), Scope.company(change.companyId()),
						Map.of("balancePiastres", change.balanceAfter(), "thresholdPiastres", threshold, "transactionId",
								transactionId.toString()));
				events.publishEvent(
						new WalletLowBalance(change.companyId(), change.balanceAfter(), threshold, transactionId));
			}
		}
		return new Posted(transactionId, List.copyOf(changes));
	}

	// ----------------------------------------------------------------------------------- helpers

	private boolean sourcePosted(SourceType sourceType, UUID sourceId) {
		return Boolean.TRUE.equals(jdbc.sql("""
				select exists (select 1 from ledger_transactions where source_type = :type and source_id = :id)
				""").param("type", sourceType.name()).param("id", sourceId).query(Boolean.class).single());
	}

	/**
	 * The account row for an account. Wallet accounts come from the locked wallet (so a wallet that
	 * does not exist was refused before anything was written); the bank account exists since V6; a
	 * station payable is opened on first use.
	 */
	private UUID accountId(Account account, Map<UUID, Wallet> wallets, Instant now) {
		if (account.kind() == Account.Kind.COMPANY_WALLET) {
			return wallets.get(account.ownerId()).accountId();
		}
		return jdbc.sql("select id from ledger_accounts where code = :code")
			.param("code", account.code())
			.query(UUID.class)
			.optional()
			.orElseGet(() -> openAccount(account, now));
	}

	private UUID openAccount(Account account, Instant now) {
		if (account.kind() != Account.Kind.STATION_PAYABLE) {
			throw new IllegalStateException("Ledger account " + account.code() + " does not exist");
		}
		// ON CONFLICT, not a caught duplicate-key error: in PostgreSQL a failed INSERT aborts the whole
		// transaction, so the caller's posting could not continue after catching it. When a concurrent
		// posting opened the account a moment ago, this inserts nothing and the select finds theirs.
		jdbc.sql("""
				insert into ledger_accounts (id, code, kind, company_id, station_id, created_at)
				values (:id, :code, 'STATION_PAYABLE', null, :station, :now)
				on conflict (code) do nothing
				""")
			.param("id", ids.next())
			.param("code", account.code())
			.param("station", account.ownerId())
			.param("now", ts(now))
			.update();
		return jdbc.sql("select id from ledger_accounts where code = :code")
			.param("code", account.code())
			.query(UUID.class)
			.single();
	}

	private void updateBalance(UUID companyId, long balance, Instant now) {
		try {
			int updated = jdbc.sql("""
					update company_wallets set balance_piastres = :balance, updated_at = :now where company_id = :company
					""").param("balance", balance).param("now", ts(now)).param("company", companyId).update();
			if (updated != 1) {
				throw new IllegalStateException("Wallet " + companyId + " vanished while locked");
			}
		}
		catch (DataIntegrityViolationException ex) {
			// The CHECK (balance_piastres >= 0) is the database's own word on the same rule.
			throw new ApiException(HttpStatus.CONFLICT, INSUFFICIENT_BALANCE, "The wallet balance is not enough");
		}
	}

	private static ApiException alreadyPosted() {
		return new ApiException(HttpStatus.CONFLICT, ALREADY_POSTED, "This source has already been posted");
	}

	static Wallet mapWallet(ResultSet rs, int row) throws SQLException {
		return new Wallet(rs.getObject("company_id", UUID.class), rs.getObject("account_id", UUID.class),
				rs.getLong("balance_piastres"), instant(rs, "updated_at"));
	}

}
