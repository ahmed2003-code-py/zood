package com.zawed.api.ledger;

import static com.zawed.api.platform.persistence.Sql.clean;
import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.Date;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;

import org.springframework.dao.DuplicateKeyException;
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
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * Top-up by bank transfer (PRD §4.6, §6.3). The company tells Operations it has transferred money
 * (amount, bank reference, date); Operations checks the bank statement and confirms, which posts
 * {@code platform:bank} → the company wallet, or rejects with a reason. Operations may also credit
 * a wallet directly against a reference when no request was raised (PRD §6.3 "Credit Wallet").
 *
 * <p>One bank transfer credits once: a reference, normalised, belongs to at most one pending or
 * confirmed request platform-wide (a unique index), and the posting's source is the request id, so
 * even a confirmed request can never post twice.
 */
@Service
class TopUps {

	enum Status {

		PENDING, CONFIRMED, REJECTED, CANCELLED

	}

	record TopUp(UUID id, UUID companyId, long amountPiastres, String bankReference, LocalDate transferDate, String note,
			Status status, UUID requestedBy, Instant requestedAt, UUID decidedBy, Instant decidedAt,
			String decisionReason, UUID ledgerTransactionId) {

		static TopUp map(ResultSet rs, int row) throws SQLException {
			Date transfer = rs.getDate("transfer_date");
			return new TopUp(rs.getObject("id", UUID.class), rs.getObject("company_id", UUID.class),
					rs.getLong("amount_piastres"), rs.getString("bank_reference"), transfer.toLocalDate(),
					rs.getString("note"), Status.valueOf(rs.getString("status")), rs.getObject("requested_by", UUID.class),
					instant(rs, "requested_at"), rs.getObject("decided_by", UUID.class), instant(rs, "decided_at"),
					rs.getString("decision_reason"), rs.getObject("ledger_transaction_id", UUID.class));
		}

	}

	/** A sanity cap on one top-up: 1,000,000,000 EGP. Above it is a typo, not a transfer. */
	static final long MAX_AMOUNT_PIASTRES = 100_000_000_000L;

	/** Transfer dates are business dates in Egypt (PRD §2.3: local time for display and rules). */
	static final ZoneId BUSINESS_ZONE = ZoneId.of("Africa/Cairo");

	private final JdbcClient jdbc;
	private final Ledger ledger;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;

	TopUps(JdbcClient jdbc, Ledger ledger, AuditLog audit, IdGenerator ids, Clock clock) {
		this.jdbc = jdbc;
		this.ledger = ledger;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
	}

	// ----------------------------------------------------------------------------------- queries

	@Transactional(readOnly = true)
	Page<TopUp> list(UUID companyId, Status status, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<TopUp> rows = jdbc.sql("""
				select * from topup_requests
				where (cast(:company as uuid) is null or company_id = :company)
				  and (cast(:status as text) is null or status = :status)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("company", companyId)
			.param("status", status == null ? null : status.name())
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(TopUp::map)
			.list();
		return Page.of(rows, size, TopUp::id);
	}

	@Transactional(readOnly = true)
	TopUp get(UUID id) {
		return jdbc.sql("select * from topup_requests where id = :id")
			.param("id", id)
			.query(TopUp::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Top-up"));
	}

	// ------------------------------------------------------------------------------ the company

	/** A company user reports a transfer. Nothing moves until Operations confirms it. */
	@Transactional
	TopUp request(ZawedPrincipal actor, UUID companyId, long amountPiastres, String bankReference,
			LocalDate transferDate, String note) {
		String reference = cleanReference(bankReference);
		checkTransferDate(transferDate);
		requireWallet(companyId);
		UUID id = ids.next();
		Instant now = clock.instant();
		try {
			jdbc.sql("""
					insert into topup_requests (id, company_id, amount_piastres, bank_reference, bank_reference_key,
					                            transfer_date, note, status, requested_by, requested_at, created_at,
					                            updated_at)
					values (:id, :company, :amount, :reference, :key, :date, :note, 'PENDING', :actor, :now, :now, :now)
					""")
				.param("id", id)
				.param("company", companyId)
				.param("amount", amountPiastres)
				.param("reference", reference)
				.param("key", referenceKey(reference))
				.param("date", Date.valueOf(transferDate))
				.param("note", clean(note))
				.param("actor", actor.userId())
				.param("now", ts(now))
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw referenceInUse();
		}
		audit.record(actorOf(actor), "TOPUP_REQUESTED", "TOPUP", id, Scope.company(companyId),
				Map.of("amountPiastres", amountPiastres, "bankReference", reference, "transferDate",
						transferDate.toString()));
		return get(id);
	}

	/** The company withdraws a request it has not yet been answered. */
	@Transactional
	TopUp cancel(ZawedPrincipal actor, UUID companyId, UUID id) {
		TopUp topUp = lock(id);
		if (!topUp.companyId().equals(companyId)) {
			throw ApiException.notFound("Top-up"); // never reached under row-level security; kept as the API rule
		}
		requirePending(topUp);
		decide(id, Status.CANCELLED, actor, null, null);
		audit.record(actorOf(actor), "TOPUP_CANCELLED", "TOPUP", id, Scope.company(companyId),
				Map.of("amountPiastres", topUp.amountPiastres(), "bankReference", topUp.bankReference()));
		return get(id);
	}

	// --------------------------------------------------------------------------------- operations

	/**
	 * Operations has seen the transfer on the bank statement: the wallet is credited, with the
	 * reference as it appears on the statement (which may correct the one the company typed).
	 */
	@Transactional
	TopUp confirm(ZawedPrincipal actor, UUID id, String bankReferenceOnStatement, String note) {
		TopUp topUp = lock(id);
		requirePending(topUp);
		String reference = topUp.bankReference();
		if (clean(bankReferenceOnStatement) != null) {
			reference = cleanReference(bankReferenceOnStatement);
			try {
				jdbc.sql("""
						update topup_requests set bank_reference = :reference, bank_reference_key = :key where id = :id
						""").param("reference", reference).param("key", referenceKey(reference)).param("id", id).update();
			}
			catch (DuplicateKeyException ex) {
				throw referenceInUse();
			}
		}
		Posted posted = ledger.post(new Posting(SourceType.TOPUP, id, reference, "Top-up by bank transfer",
				Poster.of(actor), List.of(Entry.debit(Account.platformBank(), topUp.amountPiastres()),
						Entry.credit(Account.companyWallet(topUp.companyId()), topUp.amountPiastres()))));
		decide(id, Status.CONFIRMED, actor, clean(note), posted.transactionId());
		Map<String, Object> facts = new HashMap<>();
		facts.put("amountPiastres", topUp.amountPiastres());
		facts.put("bankReference", reference);
		facts.put("transactionId", posted.transactionId().toString());
		facts.put("balanceAfterPiastres", posted.wallets().getFirst().balanceAfter());
		if (!reference.equals(topUp.bankReference())) {
			facts.put("bankReferenceRequested", topUp.bankReference());
		}
		audit.record(actorOf(actor), "TOPUP_CONFIRMED", "TOPUP", id, Scope.company(topUp.companyId()), facts);
		return get(id);
	}

	@Transactional
	TopUp reject(ZawedPrincipal actor, UUID id, String reason) {
		TopUp topUp = lock(id);
		requirePending(topUp);
		decide(id, Status.REJECTED, actor, reason, null);
		audit.record(actorOf(actor), "TOPUP_REJECTED", "TOPUP", id, Scope.company(topUp.companyId()),
				Map.of("amountPiastres", topUp.amountPiastres(), "bankReference", topUp.bankReference(), "reason", reason));
		return get(id);
	}

	/**
	 * A credit with no request behind it (PRD §6.3 "Credit Wallet action"): the same record and the
	 * same guards as a confirmed request, with nobody in {@code requestedBy}.
	 */
	@Transactional
	TopUp creditDirectly(ZawedPrincipal actor, UUID companyId, long amountPiastres, String bankReference,
			LocalDate transferDate, String note) {
		String reference = cleanReference(bankReference);
		checkTransferDate(transferDate);
		UUID id = ids.next();
		Instant now = clock.instant();
		Posted posted = ledger.post(new Posting(SourceType.TOPUP, id, reference, "Top-up by bank transfer",
				Poster.of(actor), List.of(Entry.debit(Account.platformBank(), amountPiastres),
						Entry.credit(Account.companyWallet(companyId), amountPiastres))));
		try {
			jdbc.sql("""
					insert into topup_requests (id, company_id, amount_piastres, bank_reference, bank_reference_key,
					                            transfer_date, note, status, requested_by, requested_at, decided_by,
					                            decided_at, ledger_transaction_id, created_at, updated_at)
					values (:id, :company, :amount, :reference, :key, :date, :note, 'CONFIRMED', null, :now, :actor, :now,
					        :txn, :now, :now)
					""")
				.param("id", id)
				.param("company", companyId)
				.param("amount", amountPiastres)
				.param("reference", reference)
				.param("key", referenceKey(reference))
				.param("date", Date.valueOf(transferDate))
				.param("note", clean(note))
				.param("actor", actor.userId())
				.param("txn", posted.transactionId())
				.param("now", ts(now))
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw referenceInUse(); // rolls the posting back with it
		}
		audit.record(actorOf(actor), "WALLET_CREDITED", "TOPUP", id, Scope.company(companyId),
				Map.of("amountPiastres", amountPiastres, "bankReference", reference, "transferDate",
						transferDate.toString(), "transactionId", posted.transactionId().toString(),
						"balanceAfterPiastres", posted.wallets().getFirst().balanceAfter()));
		return get(id);
	}

	// ----------------------------------------------------------------------------------- helpers

	/** Trimmed, upper case, no spaces: {@code " ab 12-x "} and {@code "AB12-X"} are the same transfer. */
	static String referenceKey(String bankReference) {
		return bankReference.strip().toUpperCase(Locale.ROOT).replaceAll("\\s+", "");
	}

	private static String cleanReference(String bankReference) {
		String reference = clean(bankReference);
		if (reference == null || referenceKey(reference).isEmpty()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_BANK_REFERENCE", "A bank reference is required",
					"bankReference", null);
		}
		return reference;
	}

	private void checkTransferDate(LocalDate transferDate) {
		LocalDate today = LocalDate.ofInstant(clock.instant(), BUSINESS_ZONE);
		if (transferDate.isAfter(today)) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "TRANSFER_DATE_IN_FUTURE", "The transfer date is in the future",
					"transferDate", null);
		}
	}

	private void requireWallet(UUID companyId) {
		jdbc.sql("select 1 from company_wallets where company_id = :company")
			.param("company", companyId)
			.query(Integer.class)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Wallet"));
	}

	/** Reads the request and holds its row until commit, so two decisions never both see PENDING. */
	private TopUp lock(UUID id) {
		return jdbc.sql("select * from topup_requests where id = :id for update")
			.param("id", id)
			.query(TopUp::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Top-up"));
	}

	private static void requirePending(TopUp topUp) {
		if (topUp.status() != Status.PENDING) {
			throw new ApiException(HttpStatus.CONFLICT, "TOPUP_NOT_PENDING", "The request has already been decided", null,
					Map.of("status", topUp.status().name()));
		}
	}

	/** The conditional flip: the row count says whether it was still pending (it is, under the lock). */
	private void decide(UUID id, Status status, ZawedPrincipal actor, String reason, UUID transactionId) {
		int updated = jdbc.sql("""
				update topup_requests set status = :status, decided_by = :actor, decided_at = :now,
				       decision_reason = :reason, ledger_transaction_id = :txn, updated_at = :now
				where id = :id and status = 'PENDING'
				""")
			.param("status", status.name())
			.param("actor", actor.userId())
			.param("now", ts(clock.instant()))
			.param("reason", reason)
			.param("txn", transactionId)
			.param("id", id)
			.update();
		if (updated != 1) {
			throw new ApiException(HttpStatus.CONFLICT, "TOPUP_NOT_PENDING", "The request has already been decided");
		}
	}

	private static ApiException referenceInUse() {
		return new ApiException(HttpStatus.CONFLICT, "BANK_REFERENCE_IN_USE",
				"A pending or confirmed top-up already carries this bank reference", "bankReference", null);
	}

	static AuditLog.Actor actorOf(ZawedPrincipal actor) {
		return new AuditLog.Actor(actor.userId(), actor.role());
	}

}
