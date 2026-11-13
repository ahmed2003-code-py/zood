package com.zawed.api.ledger;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import org.springframework.context.event.EventListener;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Propagation;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.ledger.Ledger.Account;
import com.zawed.api.ledger.Ledger.Direction;
import com.zawed.api.ledger.Ledger.SourceType;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;
import com.zawed.api.tenancy.CompanyCreated;
import com.zawed.api.tenancy.CompanyDirectory;

/**
 * Company wallets as the portals read them (PRD §6.2 Corporate Wallet, §6.3 Company Profile): the
 * balance, whether it is low, and the statement with the running balance after each entry. Opens
 * the wallet when tenancy announces a new company.
 */
@Service
class Wallets {

	record WalletView(UUID companyId, long balancePiastres, long lowBalanceThresholdPiastres, boolean lowBalance,
			Instant updatedAt) {
	}

	record StatementEntry(UUID id, long seq, Instant postedAt, Direction direction, long amountPiastres,
			long balanceAfterPiastres, SourceType sourceType, UUID sourceId, String reference, String description) {

		static StatementEntry map(ResultSet rs, int row) throws SQLException {
			return new StatementEntry(rs.getObject("id", UUID.class), rs.getLong("seq"), instant(rs, "posted_at"),
					Direction.valueOf(rs.getString("direction")), rs.getLong("amount_piastres"),
					rs.getLong("balance_after_piastres"), SourceType.valueOf(rs.getString("source_type")),
					rs.getObject("source_id", UUID.class), rs.getString("reference"), rs.getString("description"));
		}

	}

	private final JdbcClient jdbc;
	private final CompanyDirectory companies;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;

	Wallets(JdbcClient jdbc, CompanyDirectory companies, AuditLog audit, IdGenerator ids, Clock clock) {
		this.jdbc = jdbc;
		this.companies = companies;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
	}

	/**
	 * Opens the company's wallet in the onboarding transaction (the event is synchronous). The
	 * account row is the ledger's identity for the wallet; the wallet row holds the cached balance
	 * and is what the fueling engine locks.
	 */
	@EventListener
	@Transactional(propagation = Propagation.MANDATORY)
	void onCompanyCreated(CompanyCreated event) {
		UUID companyId = event.companyId();
		UUID accountId = ids.next();
		Instant now = clock.instant();
		jdbc.sql("""
				insert into ledger_accounts (id, code, kind, company_id, station_id, created_at)
				values (:id, :code, 'COMPANY_WALLET', :company, null, :now)
				""")
			.param("id", accountId)
			.param("code", Account.companyWallet(companyId).code())
			.param("company", companyId)
			.param("now", ts(now))
			.update();
		jdbc.sql("""
				insert into company_wallets (company_id, account_id, balance_piastres, created_at, updated_at)
				values (:company, :account, 0, :now, :now)
				""").param("company", companyId).param("account", accountId).param("now", ts(now)).update();
		audit.record(AuditLog.Actor.ANONYMOUS, "WALLET_CREATED", "COMPANY_WALLET", companyId, Scope.company(companyId),
				Map.of());
	}

	@Transactional(readOnly = true)
	WalletView get(UUID companyId) {
		Ledger.Wallet wallet = jdbc.sql("""
				select company_id, account_id, balance_piastres, updated_at from company_wallets where company_id = :company
				""")
			.param("company", companyId)
			.query(LedgerStore::mapWallet)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Wallet"));
		long threshold = companies.find(companyId)
			.orElseThrow(() -> ApiException.notFound("Wallet"))
			.lowBalanceThresholdPiastres();
		return new WalletView(companyId, wallet.balancePiastres(), threshold, wallet.balancePiastres() < threshold,
				wallet.updatedAt());
	}

	/**
	 * The wallet's entries, newest first, each with the balance it left. Ordered and paged by the
	 * entries' insert sequence, which for one wallet is exactly the order of its balance (V6).
	 */
	@Transactional(readOnly = true)
	Page<StatementEntry> statement(UUID companyId, String cursor, Integer limit) {
		UUID account = jdbc.sql("select account_id from company_wallets where company_id = :company")
			.param("company", companyId)
			.query(UUID.class)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Wallet"));
		int size = Page.limit(limit);
		List<StatementEntry> rows = jdbc.sql("""
				select e.id, e.seq, e.posted_at, e.direction, e.amount_piastres, e.balance_after_piastres,
				       t.source_type, t.source_id, t.reference, t.description
				from ledger_entries e join ledger_transactions t on t.id = e.transaction_id
				where e.account_id = :account and (cast(:cursor as bigint) is null or e.seq < :cursor)
				order by e.seq desc limit :limit
				""")
			.param("account", account)
			.param("cursor", seqCursor(cursor))
			.param("limit", size + 1)
			.query(StatementEntry::map)
			.list();
		if (rows.size() <= size) {
			return new Page<>(List.copyOf(rows), null);
		}
		List<StatementEntry> items = List.copyOf(rows.subList(0, size));
		return new Page<>(items, String.valueOf(items.getLast().seq()));
	}

	/** The statement's cursor is an entry sequence number, not an id. */
	static Long seqCursor(String value) {
		if (value == null || value.isBlank()) {
			return null;
		}
		try {
			return Long.parseLong(value.strip());
		}
		catch (NumberFormatException ex) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_CURSOR", "Invalid cursor", "cursor", null);
		}
	}

}
