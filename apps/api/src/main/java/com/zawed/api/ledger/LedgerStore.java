package com.zawed.api.ledger;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
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
	static final String ALREADY_REVERSED = "ALREADY_REVERSED";
	static final String INSUFFICIENT_BALANCE = "INSUFFICIENT_BALANCE";
	/** Audit actions for a company receivable: a debit raises the debt, a credit settles it. */
	static final String RECEIVABLE_RAISED = "COMPANY_RECEIVABLE_RAISED";
	static final String RECEIVABLE_SETTLED = "COMPANY_RECEIVABLE_SETTLED";

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
	public Payable lockPayable(UUID stationId) {
		return lockPayable(stationId, clock.instant());
	}

	@Override
	@Transactional(propagation = Propagation.MANDATORY)
	public Posted post(Posting posting) {
		if (posting.sourceType() == SourceType.REVERSAL) {
			throw new IllegalArgumentException("A reversal is posted with reverse(), which mirrors the original");
		}
		return post(posting, null);
	}

	@Override
	@Transactional(propagation = Propagation.MANDATORY)
	public Posted reverse(Reversal reversal) {
		// The mirror is built here from what the original actually posted, not from what a caller
		// believes it posted: same accounts, same amounts, each direction flipped, in the same order.
		List<Entry> mirror = jdbc.sql("""
				select a.kind, a.company_id, a.station_id, e.direction, e.amount_piastres
				from ledger_entries e join ledger_accounts a on a.id = e.account_id
				where e.transaction_id = :txn order by e.seq
				""").param("txn", reversal.originalTransactionId()).query((rs, n) -> {
			Account.Kind kind = Account.Kind.valueOf(rs.getString("kind"));
			UUID owner = switch (kind) {
				case PLATFORM_BANK -> null;
				case COMPANY_WALLET, COMPANY_RECEIVABLE -> rs.getObject("company_id", UUID.class);
				case STATION_PAYABLE -> rs.getObject("station_id", UUID.class);
			};
			Direction flipped = Direction.valueOf(rs.getString("direction")) == Direction.DEBIT ? Direction.CREDIT
					: Direction.DEBIT;
			return new Entry(new Account(kind, owner), flipped, rs.getLong("amount_piastres"));
		}).list();
		if (mirror.isEmpty()) {
			throw ApiException.notFound("Ledger transaction");
		}
		return post(new Posting(SourceType.REVERSAL, reversal.reversalId(), reversal.reference(), reversal.description(),
				reversal.poster(), mirror), reversal.originalTransactionId());
	}

	/**
	 * @param reverses the transaction this posting mirrors, for a reversal; null otherwise
	 */
	private Posted post(Posting posting, UUID reverses) {
		Instant now = clock.instant();

		// 1. Lock every wallet the posting touches, always in ascending company id, so two postings
		//    that touch the same wallets can never wait on each other in a cycle. Then every station
		//    payable, in ascending station id. Wallets always come before payables (ADR 0020): a
		//    fueling holds its company's wallet when it reaches here and a payout holds no wallet at
		//    all, so no two postings can wait on each other in a cycle.
		TreeMap<UUID, Wallet> wallets = new TreeMap<>();
		TreeMap<UUID, Payable> payables = new TreeMap<>();
		for (Entry entry : posting.entries()) {
			if (entry.account().kind() == Account.Kind.COMPANY_WALLET) {
				wallets.putIfAbsent(entry.account().ownerId(), null);
			}
			else if (entry.account().kind() == Account.Kind.STATION_PAYABLE) {
				payables.putIfAbsent(entry.account().ownerId(), null);
			}
		}
		for (UUID companyId : wallets.keySet()) {
			wallets.put(companyId, lockWallet(companyId));
		}
		for (UUID stationId : payables.keySet()) {
			payables.put(stationId, lockPayable(stationId, now));
		}

		// 2. A source posts once, and a transaction is reversed once. Checked here, after the locks,
		//    so a concurrent duplicate (which waited on the same wallet or payable lock) sees the
		//    first posting and is refused without aborting the caller's database transaction; the
		//    unique indexes below are the final guard.
		if (sourcePosted(posting.sourceType(), posting.sourceId())) {
			throw alreadyPosted();
		}
		if (reverses != null && reversed(reverses)) {
			throw alreadyReversed();
		}

		// 3. Accounts, then the transaction row.
		Map<Account, UUID> accountIds = new HashMap<>();
		for (Entry entry : posting.entries()) {
			accountIds.computeIfAbsent(entry.account(), (account) -> accountId(account, wallets, payables, now));
		}
		UUID transactionId = ids.next();
		// The one company whose wallet or receivable the posting touches, so that company can read
		// the transaction under row-level security; null when there is none or more than one.
		Set<UUID> companyIds = new HashSet<>();
		for (Entry entry : posting.entries()) {
			if (entry.account().companyId() != null) {
				companyIds.add(entry.account().companyId());
			}
		}
		UUID companyId = companyIds.size() == 1 ? companyIds.iterator().next() : null;
		try {
			jdbc.sql("""
					insert into ledger_transactions (id, source_type, source_id, company_id, reference, description,
					                                 posted_at, posted_by, reverses_transaction_id)
					values (:id, :sourceType, :sourceId, :company, :reference, :description, :now, :actor, :reverses)
					""")
				.param("id", transactionId)
				.param("sourceType", posting.sourceType().name())
				.param("sourceId", posting.sourceId())
				.param("company", companyId)
				.param("reference", posting.reference())
				.param("description", posting.description())
				.param("now", ts(now))
				.param("actor", posting.poster().userId())
				.param("reverses", reverses)
				.update();
		}
		catch (DuplicateKeyException ex) {
			// Two unique rules can refuse the row: one posting per source, one reversal per original.
			throw String.valueOf(ex.getMessage()).contains("ledger_transactions_reversed_once") ? alreadyReversed()
					: alreadyPosted();
		}

		// 4. The entries, in order. A wallet or payable entry moves the cached balance under the
		//    lock taken in step 1 and records the balance it leaves behind.
		Map<UUID, Long> balances = new HashMap<>();
		wallets.forEach((id, wallet) -> balances.put(id, wallet.balancePiastres()));
		Map<UUID, Long> owed = new HashMap<>();
		payables.forEach((id, payable) -> owed.put(id, payable.balancePiastres()));
		for (Entry entry : posting.entries()) {
			Long balanceAfter = null;
			// Wallet and receivable entries carry their company (the V6 trigger checks it against the
			// account). Only a wallet entry has a cached balance to move and a floor to respect.
			UUID entryCompany = entry.account().companyId();
			if (entry.account().kind() == Account.Kind.COMPANY_WALLET) {
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
			else if (entry.account().kind() == Account.Kind.STATION_PAYABLE) {
				// A payable is a liability: a credit raises what the station is owed, a debit (a payout,
				// a reversal) lowers it. There is no floor: below zero the station owes the platform.
				UUID stationId = entry.account().ownerId();
				long before = owed.get(stationId);
				long after = entry.direction() == Direction.CREDIT ? Math.addExact(before, entry.piastres())
						: Math.subtractExact(before, entry.piastres());
				updatePayable(stationId, after, now);
				owed.put(stationId, after);
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

		// 5. A company's debt never changes silently: every receivable entry is audited with the
		//    posting that made it (B5: fuel taken offline that the wallet could not cover).
		for (Entry entry : posting.entries()) {
			if (entry.account().kind() == Account.Kind.COMPANY_RECEIVABLE) {
				UUID debtor = entry.account().ownerId();
				audit.record(new AuditLog.Actor(posting.poster().userId(), posting.poster().role()),
						entry.direction() == Direction.DEBIT ? RECEIVABLE_RAISED : RECEIVABLE_SETTLED, "COMPANY_RECEIVABLE",
						debtor, Scope.company(debtor), Map.of("amountPiastres", entry.piastres(), "sourceType",
								posting.sourceType().name(), "sourceId", posting.sourceId().toString(), "transactionId",
								transactionId.toString()));
			}
		}

		// 6. Low balance (PRD 4.7): recorded once, when a posting crosses the threshold downwards.
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
		List<PayableChange> payableChanges = new ArrayList<>();
		payables.forEach((stationId, payable) -> payableChanges
			.add(new PayableChange(stationId, payable.balancePiastres(), owed.get(stationId))));
		return new Posted(transactionId, List.copyOf(changes), List.copyOf(payableChanges));
	}

	// ----------------------------------------------------------------------------------- helpers

	private boolean sourcePosted(SourceType sourceType, UUID sourceId) {
		return Boolean.TRUE.equals(jdbc.sql("""
				select exists (select 1 from ledger_transactions where source_type = :type and source_id = :id)
				""").param("type", sourceType.name()).param("id", sourceId).query(Boolean.class).single());
	}

	private boolean reversed(UUID transactionId) {
		return Boolean.TRUE.equals(jdbc.sql("""
				select exists (select 1 from ledger_transactions where reverses_transaction_id = :id)
				""").param("id", transactionId).query(Boolean.class).single());
	}

	/**
	 * The station's payable row, locked; opened on first use. The row carries the station's operator
	 * (for row-level security) and starts at the sum of the account's entries, which is zero for a
	 * new account and keeps a payable opened before V9 right if its row were ever missing.
	 */
	private Payable lockPayable(UUID stationId, Instant now) {
		Optional<Payable> existing = selectPayableForUpdate(stationId);
		if (existing.isPresent()) {
			return existing.get();
		}
		UUID accountId = jdbc.sql("select id from ledger_accounts where code = :code")
			.param("code", Account.stationPayable(stationId).code())
			.query(UUID.class)
			.optional()
			.orElseGet(() -> {
				requireStation(stationId);
				return openAccount(Account.stationPayable(stationId), now);
			});
		// ON CONFLICT for the same reason as openAccount: two first postings to one station race here,
		// and the loser must find the winner's row instead of aborting its transaction.
		jdbc.sql("""
				insert into station_payables (station_id, operator_id, account_id, balance_piastres, created_at, updated_at)
				select s.id, s.operator_id, :account,
				       coalesce((select sum(case e.direction when 'CREDIT' then e.amount_piastres else -e.amount_piastres end)
				                   from ledger_entries e where e.account_id = :account), 0),
				       :now, :now
				from stations s where s.id = :station
				on conflict do nothing
				""").param("station", stationId).param("account", accountId).param("now", ts(now)).update();
		return selectPayableForUpdate(stationId).orElseThrow(() -> ApiException.notFound("Station"));
	}

	private Optional<Payable> selectPayableForUpdate(UUID stationId) {
		return jdbc.sql("""
				select station_id, account_id, balance_piastres, updated_at from station_payables
				where station_id = :station for update
				""")
			.param("station", stationId)
			.query((rs, n) -> new Payable(rs.getObject("station_id", UUID.class), rs.getObject("account_id", UUID.class),
					rs.getLong("balance_piastres"), instant(rs, "updated_at")))
			.optional();
	}

	private void requireStation(UUID stationId) {
		jdbc.sql("select 1 from stations where id = :id")
			.param("id", stationId)
			.query(Integer.class)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Station"));
	}

	/**
	 * The account row for an account. Wallet and payable accounts come from their locked rows (so one
	 * that does not exist was refused before anything was written); the bank account exists since V6;
	 * a company receivable is opened on first use.
	 */
	private UUID accountId(Account account, Map<UUID, Wallet> wallets, Map<UUID, Payable> payables, Instant now) {
		if (account.kind() == Account.Kind.COMPANY_WALLET) {
			return wallets.get(account.ownerId()).accountId();
		}
		if (account.kind() == Account.Kind.STATION_PAYABLE) {
			return payables.get(account.ownerId()).accountId();
		}
		return jdbc.sql("select id from ledger_accounts where code = :code")
			.param("code", account.code())
			.query(UUID.class)
			.optional()
			.orElseGet(() -> openAccount(account, now));
	}

	private UUID openAccount(Account account, Instant now) {
		if (account.kind() != Account.Kind.STATION_PAYABLE && account.kind() != Account.Kind.COMPANY_RECEIVABLE) {
			throw new IllegalStateException("Ledger account " + account.code() + " does not exist");
		}
		// ON CONFLICT, not a caught duplicate-key error: in PostgreSQL a failed INSERT aborts the whole
		// transaction, so the caller's posting could not continue after catching it. When a concurrent
		// posting opened the account a moment ago, this inserts nothing and the select finds theirs.
		// No conflict target: the code and (company, kind) indexes describe the same account, and a
		// race can trip either one first.
		jdbc.sql("""
				insert into ledger_accounts (id, code, kind, company_id, station_id, created_at)
				values (:id, :code, :kind, :company, :station, :now)
				on conflict do nothing
				""")
			.param("id", ids.next())
			.param("code", account.code())
			.param("kind", account.kind().name())
			.param("company", account.companyId())
			.param("station", account.kind() == Account.Kind.STATION_PAYABLE ? account.ownerId() : null)
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

	private void updatePayable(UUID stationId, long balance, Instant now) {
		int updated = jdbc.sql("""
				update station_payables set balance_piastres = :balance, updated_at = :now where station_id = :station
				""").param("balance", balance).param("now", ts(now)).param("station", stationId).update();
		if (updated != 1) {
			throw new IllegalStateException("Payable of station " + stationId + " vanished while locked");
		}
	}

	private static ApiException alreadyPosted() {
		return new ApiException(HttpStatus.CONFLICT, ALREADY_POSTED, "This source has already been posted");
	}

	private static ApiException alreadyReversed() {
		return new ApiException(HttpStatus.CONFLICT, ALREADY_REVERSED, "This transaction has already been reversed");
	}

	static Wallet mapWallet(ResultSet rs, int row) throws SQLException {
		return new Wallet(rs.getObject("company_id", UUID.class), rs.getObject("account_id", UUID.class),
				rs.getLong("balance_piastres"), instant(rs, "updated_at"));
	}

}
