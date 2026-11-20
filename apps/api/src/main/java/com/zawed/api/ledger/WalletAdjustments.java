package com.zawed.api.ledger;

import static com.zawed.api.ledger.TopUps.actorOf;
import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import org.springframework.context.ApplicationEventPublisher;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.ledger.Ledger.Account;
import com.zawed.api.ledger.Ledger.Entry;
import com.zawed.api.ledger.Ledger.Posted;
import com.zawed.api.ledger.Ledger.Poster;
import com.zawed.api.ledger.Ledger.Posting;
import com.zawed.api.ledger.Ledger.SourceType;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.persistence.IdempotencyKeys;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * Wallet credits that are not bank transfers (V12, ADR 0021): a goodwill credit, a correction. They
 * are the exception, so they are never a shortcut: one Operations user asks with a reason, a
 * <b>different</b> user holding {@code TOPUP_APPROVE} approves, and only then does the ledger post
 * {@code platform:adjustments} → the wallet. The adjustment account keeps them apart from real bank
 * money. Credits only: taking money out of a wallet is a reversal of what put it there.
 */
@Service
class WalletAdjustments {

	enum Status {

		PENDING_APPROVAL, APPROVED, REJECTED, CANCELLED

	}

	record Adjustment(UUID id, UUID companyId, long amountPiastres, String reason, Status status, UUID requestedBy,
			Instant requestedAt, UUID decidedBy, Instant decidedAt, String decisionReason, UUID ledgerTransactionId) {

		static Adjustment map(ResultSet rs, int row) throws SQLException {
			return new Adjustment(rs.getObject("id", UUID.class), rs.getObject("company_id", UUID.class),
					rs.getLong("amount_piastres"), rs.getString("reason"), Status.valueOf(rs.getString("status")),
					rs.getObject("requested_by", UUID.class), instant(rs, "requested_at"),
					rs.getObject("decided_by", UUID.class), instant(rs, "decided_at"), rs.getString("decision_reason"),
					rs.getObject("ledger_transaction_id", UUID.class));
		}

	}

	private final JdbcClient jdbc;
	private final Ledger ledger;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;
	private final IdempotencyKeys keys;
	private final ApplicationEventPublisher events;

	WalletAdjustments(JdbcClient jdbc, Ledger ledger, AuditLog audit, IdGenerator ids, Clock clock,
			IdempotencyKeys keys, ApplicationEventPublisher events) {
		this.keys = keys;
		this.events = events;
		this.jdbc = jdbc;
		this.ledger = ledger;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
	}

	@Transactional(readOnly = true)
	Page<Adjustment> list(UUID companyId, Status status, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Adjustment> rows = jdbc.sql("""
				select * from wallet_adjustments
				where (cast(:company as uuid) is null or company_id = :company)
				  and (cast(:status as text) is null or status = :status)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("company", companyId)
			.param("status", status == null ? null : status.name())
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Adjustment::map)
			.list();
		return Page.of(rows, size, Adjustment::id);
	}

	@Transactional(readOnly = true)
	Adjustment get(UUID id) {
		return jdbc.sql("select * from wallet_adjustments where id = :id")
			.param("id", id)
			.query(Adjustment::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Adjustment"));
	}

	/**
	 * Asks for a credit. Nothing moves until a different person approves. The Idempotency-Key is
	 * mandatory (ADR 0022): an adjustment has no natural key, so without one a retried request would be
	 * a second pending adjustment that an approver could approve as well, crediting the wallet twice.
	 * A repeat with the same key answers the adjustment it created.
	 */
	@Transactional
	Adjustment request(ZawedPrincipal actor, UUID companyId, String idempotencyKey, Object body, long amountPiastres,
			String reason) {
		UUID id = ids.next();
		java.util.Optional<UUID> earlier = keys.claimRequired(actor.userId(), "wallet_adjustment.create", idempotencyKey,
				java.util.List.of(companyId, body), id);
		if (earlier.isPresent()) {
			return get(earlier.get());
		}
		jdbc.sql("select 1 from company_wallets where company_id = :company")
			.param("company", companyId)
			.query(Integer.class)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Wallet"));
		Instant now = clock.instant();
		jdbc.sql("""
				insert into wallet_adjustments (id, company_id, amount_piastres, reason, status, requested_by, requested_at,
				                                created_at, updated_at)
				values (:id, :company, :amount, :reason, 'PENDING_APPROVAL', :actor, :now, :now, :now)
				""")
			.param("id", id)
			.param("company", companyId)
			.param("amount", amountPiastres)
			.param("reason", reason.strip())
			.param("actor", actor.userId())
			.param("now", ts(now))
			.update();
		audit.record(actorOf(actor), "WALLET_ADJUSTMENT_REQUESTED", "WALLET_ADJUSTMENT", id, Scope.company(companyId),
				Map.of("amountPiastres", amountPiastres, "reason", reason.strip()));
		events.publishEvent(new WalletAdjustmentAwaitingApproval(id, companyId, amountPiastres));
		return get(id);
	}

	/** A different person approves: the wallet is credited once. A repeat answers it as it is. */
	@Transactional
	Adjustment approve(ZawedPrincipal actor, UUID id) {
		Adjustment adjustment = lock(id);
		if (adjustment.status() == Status.APPROVED) {
			return adjustment;
		}
		requirePending(adjustment);
		if (actor.userId().equals(adjustment.requestedBy())) {
			throw new ApiException(HttpStatus.FORBIDDEN, "SELF_APPROVAL_NOT_ALLOWED",
					"The person who asked for an adjustment cannot approve it");
		}
		ledger.lockWallet(adjustment.companyId());
		Posted posted = ledger.post(new Posting(SourceType.ADJUSTMENT, id, "ADJ-" + id.toString().substring(0, 8),
				"Wallet adjustment: " + adjustment.reason(), Poster.of(actor),
				List.of(Entry.debit(Account.platformAdjustments(), adjustment.amountPiastres()),
						Entry.credit(Account.companyWallet(adjustment.companyId()), adjustment.amountPiastres()))));
		decide(id, Status.APPROVED, actor, null, posted.transactionId());
		audit.record(actorOf(actor), "WALLET_ADJUSTMENT_APPROVED", "WALLET_ADJUSTMENT", id,
				Scope.company(adjustment.companyId()),
				Map.of("amountPiastres", adjustment.amountPiastres(), "requestedBy", adjustment.requestedBy().toString(),
						"transactionId", posted.transactionId().toString(), "balanceAfterPiastres",
						posted.wallets().getFirst().balanceAfter()));
		return get(id);
	}

	@Transactional
	Adjustment reject(ZawedPrincipal actor, UUID id, String reason) {
		Adjustment adjustment = lock(id);
		requirePending(adjustment);
		decide(id, Status.REJECTED, actor, reason, null);
		audit.record(actorOf(actor), "WALLET_ADJUSTMENT_REJECTED", "WALLET_ADJUSTMENT", id,
				Scope.company(adjustment.companyId()), Map.of("amountPiastres", adjustment.amountPiastres(), "reason", reason));
		return get(id);
	}

	/** The requester withdraws an adjustment nobody has decided. */
	@Transactional
	Adjustment cancel(ZawedPrincipal actor, UUID id) {
		Adjustment adjustment = lock(id);
		requirePending(adjustment);
		if (!actor.userId().equals(adjustment.requestedBy())) {
			throw new ApiException(HttpStatus.FORBIDDEN, "FORBIDDEN", "Only the requester withdraws an adjustment");
		}
		decide(id, Status.CANCELLED, actor, null, null);
		audit.record(actorOf(actor), "WALLET_ADJUSTMENT_CANCELLED", "WALLET_ADJUSTMENT", id,
				Scope.company(adjustment.companyId()), Map.of("amountPiastres", adjustment.amountPiastres()));
		return get(id);
	}

	private Adjustment lock(UUID id) {
		return jdbc.sql("select * from wallet_adjustments where id = :id for update")
			.param("id", id)
			.query(Adjustment::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Adjustment"));
	}

	private static void requirePending(Adjustment adjustment) {
		if (adjustment.status() != Status.PENDING_APPROVAL) {
			throw new ApiException(HttpStatus.CONFLICT, "ADJUSTMENT_NOT_PENDING", "The adjustment has already been decided",
					null, Map.of("status", adjustment.status().name()));
		}
	}

	private void decide(UUID id, Status status, ZawedPrincipal actor, String reason, UUID transactionId) {
		int updated = jdbc.sql("""
				update wallet_adjustments set status = :status, decided_by = :actor, decided_at = :now,
				       decision_reason = :reason, ledger_transaction_id = :txn, updated_at = :now
				where id = :id and status = 'PENDING_APPROVAL'
				""")
			.param("status", status.name())
			.param("actor", actor.userId())
			.param("now", ts(clock.instant()))
			.param("reason", reason)
			.param("txn", transactionId)
			.param("id", id)
			.update();
		if (updated != 1) {
			throw new ApiException(HttpStatus.CONFLICT, "ADJUSTMENT_NOT_PENDING", "The adjustment has already been decided");
		}
	}

}
