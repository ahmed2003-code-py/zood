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
import java.util.Set;
import java.util.UUID;
import java.util.stream.Collectors;

import org.springframework.beans.factory.annotation.Value;
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
 * Top-up by bank transfer (PRD §4.6, §6.3; ADR 0021, maker-checker). Money enters a wallet in three
 * steps, and two different people take the last two:
 *
 * <ol>
 * <li>The company says it has transferred money (amount, reference, date): {@code PENDING}. This is
 * a claim, never proof, and nothing moves. Operations may also record a transfer the company never
 * announced: that row starts at step 2.
 * <li>An Operations user finds the transfer on ZAWED's bank statement and <b>verifies</b> it:
 * which receiving account, the reference as the statement shows it, and the amount that actually
 * arrived: {@code PENDING_APPROVAL}.
 * <li>A <b>different</b> user holding {@code TOPUP_APPROVE} approves: {@code CONFIRMED}, posting
 * {@code platform:bank} → the company wallet for exactly the verified amount, never the claimed one.
 * </ol>
 *
 * <p>One bank transfer credits once: a reference, normalised, belongs to at most one live top-up per
 * receiving account (a unique index), and the posting's source is the top-up id, so even a confirmed
 * top-up can never post twice. The two-person rule and "the verifier and the approver are the acting
 * users" are also database rules (V12).
 */
@Service
class TopUps {

	enum Status {

		PENDING, PENDING_APPROVAL, CONFIRMED, REJECTED, CANCELLED

	}

	enum Origin {

		COMPANY_REQUEST, OPS_RECORDED

	}

	record TopUp(UUID id, UUID companyId, Origin origin, long amountPiastres, String receivingAccount,
			String bankReference, LocalDate transferDate, String note, Status status, UUID requestedBy,
			Instant requestedAt, Long verifiedAmountPiastres, UUID verifiedBy, Instant verifiedAt,
			String evidenceReference, UUID decidedBy, Instant decidedAt, String decisionReason, UUID ledgerTransactionId) {

		static TopUp map(ResultSet rs, int row) throws SQLException {
			Date transfer = rs.getDate("transfer_date");
			Long verified = rs.getObject("verified_amount_piastres", Long.class);
			return new TopUp(rs.getObject("id", UUID.class), rs.getObject("company_id", UUID.class),
					Origin.valueOf(rs.getString("origin")), rs.getLong("amount_piastres"), rs.getString("receiving_account"),
					rs.getString("bank_reference"), transfer.toLocalDate(), rs.getString("note"),
					Status.valueOf(rs.getString("status")), rs.getObject("requested_by", UUID.class),
					instant(rs, "requested_at"), verified, rs.getObject("verified_by", UUID.class),
					instant(rs, "verified_at"), rs.getString("evidence_reference"), rs.getObject("decided_by", UUID.class),
					instant(rs, "decided_at"), rs.getString("decision_reason"),
					rs.getObject("ledger_transaction_id", UUID.class));
		}

	}

	/** What Operations read on the bank statement. */
	record Verification(String receivingAccount, String bankReference, long receivedAmountPiastres,
			LocalDate transferDate, String evidenceReference, String note) {
	}

	/** A sanity cap on one top-up: 1,000,000,000 EGP. Above it is a typo, not a transfer. */
	static final long MAX_AMOUNT_PIASTRES = 100_000_000_000L;

	/** Transfer dates are business dates in Egypt (PRD §2.3: local time for display and rules). */
	static final ZoneId BUSINESS_ZONE = ZoneId.of("Africa/Cairo");

	static final String DEFAULT_RECEIVING_ACCOUNT = "MAIN";

	private final JdbcClient jdbc;
	private final Ledger ledger;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;
	private final Set<String> receivingAccounts;

	TopUps(JdbcClient jdbc, Ledger ledger, AuditLog audit, IdGenerator ids, Clock clock,
			@Value("${zawed.finance.receiving-accounts:MAIN}") List<String> receivingAccounts) {
		this.jdbc = jdbc;
		this.ledger = ledger;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
		this.receivingAccounts = receivingAccounts.stream()
			.map((a) -> a.trim().toUpperCase(Locale.ROOT))
			.filter((a) -> !a.isEmpty())
			.collect(Collectors.toUnmodifiableSet());
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

	/** The receiving accounts Operations may name, for the Ops screen. */
	Set<String> receivingAccounts() {
		return receivingAccounts;
	}

	// ------------------------------------------------------------------------------ the company

	/** A company user reports a transfer. A claim: nothing moves until two Operations users agree. */
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
					insert into topup_requests (id, company_id, origin, amount_piastres, receiving_account, bank_reference,
					                            bank_reference_key, transfer_date, note, status, requested_by, requested_at,
					                            created_at, updated_at)
					values (:id, :company, 'COMPANY_REQUEST', :amount, :account, :reference, :key, :date, :note, 'PENDING',
					        :actor, :now, :now, :now)
					""")
				.param("id", id)
				.param("company", companyId)
				.param("amount", amountPiastres)
				.param("account", DEFAULT_RECEIVING_ACCOUNT)
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

	/** The company withdraws a request nobody has verified yet. */
	@Transactional
	TopUp cancel(ZawedPrincipal actor, UUID companyId, UUID id) {
		// Read first: a company's row lock needs its UPDATE policy, which only a PENDING request passes,
		// so a verified or decided request would otherwise answer 404 instead of saying why.
		requireStatus(get(id), Status.PENDING);
		TopUp topUp = lock(id);
		if (!topUp.companyId().equals(companyId)) {
			throw ApiException.notFound("Top-up"); // never reached under row-level security; kept as the API rule
		}
		requireStatus(topUp, Status.PENDING);
		decide(id, Status.PENDING, Status.CANCELLED, actor, null, null);
		audit.record(actorOf(actor), "TOPUP_CANCELLED", "TOPUP", id, Scope.company(companyId),
				Map.of("amountPiastres", topUp.amountPiastres(), "bankReference", topUp.bankReference()));
		return get(id);
	}

	// ------------------------------------------------------------------------- step 2: verify

	/**
	 * Operations has found the company's transfer on ZAWED's statement and records what arrived. The
	 * reference may be corrected to the statement's; the amount is what the bank shows.
	 */
	@Transactional
	TopUp verify(ZawedPrincipal actor, UUID id, Verification v) {
		TopUp topUp = lock(id);
		requireStatus(topUp, Status.PENDING);
		String account = receivingAccount(v.receivingAccount());
		String reference = clean(v.bankReference()) == null ? topUp.bankReference() : cleanReference(v.bankReference());
		LocalDate transferDate = v.transferDate() == null ? topUp.transferDate() : v.transferDate();
		checkTransferDate(transferDate);
		Instant now = clock.instant();
		try {
			int updated = jdbc.sql("""
					update topup_requests
					set status = 'PENDING_APPROVAL', receiving_account = :account, bank_reference = :reference,
					    bank_reference_key = :key, transfer_date = :date, verified_amount_piastres = :amount,
					    verified_by = :actor, verified_at = :now, evidence_reference = :evidence,
					    note = coalesce(:note, note), updated_at = :now
					where id = :id and status = 'PENDING'
					""")
				.param("account", account)
				.param("reference", reference)
				.param("key", referenceKey(reference))
				.param("date", Date.valueOf(transferDate))
				.param("amount", v.receivedAmountPiastres())
				.param("actor", actor.userId())
				.param("now", ts(now))
				.param("evidence", clean(v.evidenceReference()))
				.param("note", clean(v.note()))
				.param("id", id)
				.update();
			if (updated != 1) {
				throw notInStatus(Status.PENDING, topUp.status());
			}
		}
		catch (DuplicateKeyException ex) {
			throw referenceInUse();
		}
		Map<String, Object> facts = new HashMap<>();
		facts.put("claimedAmountPiastres", topUp.amountPiastres());
		facts.put("verifiedAmountPiastres", v.receivedAmountPiastres());
		facts.put("receivingAccount", account);
		facts.put("bankReference", reference);
		if (!reference.equals(topUp.bankReference())) {
			facts.put("bankReferenceRequested", topUp.bankReference());
		}
		if (clean(v.evidenceReference()) != null) {
			facts.put("evidenceReference", clean(v.evidenceReference()));
		}
		audit.record(actorOf(actor), "TOPUP_VERIFIED", "TOPUP", id, Scope.company(topUp.companyId()), facts);
		return get(id);
	}

	/**
	 * Operations records a transfer that arrived without a company request (PRD §6.3 "Credit
	 * Wallet"). It is verified by the recorder and still waits for a second person. Sending the same
	 * record again answers the existing record ({@code replayed}): the same account and reference, and
	 * the same company, amount, transfer date, evidence and note, on a record Operations made. The same
	 * reference with anything else different is {@code 409 BANK_REFERENCE_IN_USE}, never a second
	 * top-up and never another request's record passed off as this one.
	 */
	@Transactional
	Recorded recordTransfer(ZawedPrincipal actor, UUID companyId, Verification v) {
		String account = receivingAccount(v.receivingAccount());
		String reference = cleanReference(v.bankReference());
		if (v.transferDate() == null) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "VALIDATION_FAILED", "The transfer date is required",
					"transferDate", null);
		}
		checkTransferDate(v.transferDate());
		requireWallet(companyId);
		UUID id = ids.next();
		Instant now = clock.instant();
		// ON CONFLICT, not a caught duplicate-key error: a failed INSERT aborts the whole transaction in
		// PostgreSQL, and the replay below must still read the existing record.
		int inserted = jdbc.sql("""
				insert into topup_requests (id, company_id, origin, amount_piastres, receiving_account, bank_reference,
				                            bank_reference_key, transfer_date, note, status, requested_by, requested_at,
				                            verified_amount_piastres, verified_by, verified_at, evidence_reference,
				                            created_at, updated_at)
				values (:id, :company, 'OPS_RECORDED', :amount, :account, :reference, :key, :date, :note,
				        'PENDING_APPROVAL', null, :now, :amount, :actor, :now, :evidence, :now, :now)
				on conflict do nothing
				""")
			.param("id", id)
			.param("company", companyId)
			.param("amount", v.receivedAmountPiastres())
			.param("account", account)
			.param("reference", reference)
			.param("key", referenceKey(reference))
			.param("date", Date.valueOf(v.transferDate()))
			.param("note", clean(v.note()))
			.param("actor", actor.userId())
			.param("evidence", clean(v.evidenceReference()))
			.param("now", ts(now))
			.update();
		if (inserted == 0) {
			TopUp live = liveByReference(account, referenceKey(reference)).orElseThrow(TopUps::referenceInUse);
			// A replay is this request sent again, field for field (review finding, B7 W1): a retry that
			// differs in its date, evidence or note is another claim about the transfer, and a company's
			// verified request is not an Ops record. The typed reference may differ in spelling only (its
			// key is equal), which the statement cannot tell apart either.
			boolean same = live.origin() == Origin.OPS_RECORDED && live.companyId().equals(companyId)
					&& live.verifiedAmountPiastres() != null && live.verifiedAmountPiastres() == v.receivedAmountPiastres()
					&& live.transferDate().equals(v.transferDate())
					&& java.util.Objects.equals(live.evidenceReference(), clean(v.evidenceReference()))
					&& java.util.Objects.equals(live.note(), clean(v.note()));
			if (!same) {
				throw referenceInUse();
			}
			return new Recorded(live, true);
		}
		audit.record(actorOf(actor), "TOPUP_RECORDED", "TOPUP", id, Scope.company(companyId),
				Map.of("verifiedAmountPiastres", v.receivedAmountPiastres(), "receivingAccount", account, "bankReference",
						reference, "transferDate", v.transferDate().toString()));
		return new Recorded(get(id), false);
	}

	record Recorded(TopUp topUp, boolean replayed) {
	}

	// ------------------------------------------------------------------------ step 3: approve

	/**
	 * A different person approves a verified top-up: the wallet is credited the verified amount, once.
	 * Approving a top-up that is already confirmed answers it as it is and posts nothing.
	 */
	@Transactional
	TopUp approve(ZawedPrincipal actor, UUID id) {
		TopUp topUp = lock(id);
		if (topUp.status() == Status.CONFIRMED) {
			return topUp; // a repeated approval (a retry, a double click): already credited, exactly once
		}
		requireStatus(topUp, Status.PENDING_APPROVAL);
		if (actor.userId().equals(topUp.verifiedBy())) {
			throw new ApiException(HttpStatus.FORBIDDEN, "SELF_APPROVAL_NOT_ALLOWED",
					"The person who verified a transfer cannot approve it");
		}
		// The wallet after the top-up row: every path that credits a wallet takes the request row first
		// and the wallet second, and a fueling takes only the wallet, so no cycle can form.
		ledger.lockWallet(topUp.companyId());
		long amount = topUp.verifiedAmountPiastres();
		Posted posted = ledger.post(new Posting(SourceType.TOPUP, id, topUp.bankReference(), "Top-up by bank transfer",
				Poster.of(actor), List.of(Entry.debit(Account.platformBank(), amount),
						Entry.credit(Account.companyWallet(topUp.companyId()), amount))));
		decide(id, Status.PENDING_APPROVAL, Status.CONFIRMED, actor, null, posted.transactionId());
		Map<String, Object> facts = new HashMap<>();
		facts.put("creditedPiastres", amount);
		facts.put("claimedAmountPiastres", topUp.amountPiastres());
		facts.put("verifiedBy", topUp.verifiedBy().toString());
		facts.put("bankReference", topUp.bankReference());
		facts.put("receivingAccount", topUp.receivingAccount());
		facts.put("transactionId", posted.transactionId().toString());
		facts.put("balanceAfterPiastres", posted.wallets().getFirst().balanceAfter());
		audit.record(actorOf(actor), "TOPUP_CONFIRMED", "TOPUP", id, Scope.company(topUp.companyId()), facts);
		return get(id);
	}

	/** Refuses a claim (no matching transfer) or a verification (the approver disagrees). */
	@Transactional
	TopUp reject(ZawedPrincipal actor, UUID id, String reason) {
		TopUp topUp = lock(id);
		if (topUp.status() != Status.PENDING && topUp.status() != Status.PENDING_APPROVAL) {
			throw notInStatus(Status.PENDING, topUp.status());
		}
		decide(id, topUp.status(), Status.REJECTED, actor, reason, null);
		audit.record(actorOf(actor), "TOPUP_REJECTED", "TOPUP", id, Scope.company(topUp.companyId()),
				Map.of("amountPiastres", topUp.amountPiastres(), "bankReference", topUp.bankReference(), "reason", reason,
						"stage", topUp.status().name()));
		return get(id);
	}

	// ----------------------------------------------------------------------------------- helpers

	/** The comparison form of a bank reference; see {@link BankReferences#key}. */
	static String referenceKey(String bankReference) {
		return BankReferences.key(bankReference);
	}

	private static String cleanReference(String bankReference) {
		return BankReferences.clean(bankReference);
	}

	private String receivingAccount(String requested) {
		String account = clean(requested) == null ? DEFAULT_RECEIVING_ACCOUNT : requested.trim().toUpperCase(Locale.ROOT);
		if (!receivingAccounts.contains(account)) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "UNKNOWN_RECEIVING_ACCOUNT",
					"This is not one of ZAWED's receiving bank accounts", "receivingAccount",
					Map.of("accounts", receivingAccounts.stream().sorted().toList()));
		}
		return account;
	}

	private java.util.Optional<TopUp> liveByReference(String account, String key) {
		return jdbc.sql("""
				select * from topup_requests
				where receiving_account = :account and bank_reference_key = :key
				  and status in ('PENDING', 'PENDING_APPROVAL', 'CONFIRMED')
				""").param("account", account).param("key", key).query(TopUp::map).optional();
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

	/** Reads the top-up and holds its row until commit, so two decisions never both see it open. */
	private TopUp lock(UUID id) {
		return jdbc.sql("select * from topup_requests where id = :id for update")
			.param("id", id)
			.query(TopUp::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Top-up"));
	}

	private static void requireStatus(TopUp topUp, Status expected) {
		if (topUp.status() != expected) {
			throw notInStatus(expected, topUp.status());
		}
	}

	private static ApiException notInStatus(Status expected, Status actual) {
		String code = expected == Status.PENDING_APPROVAL ? "TOPUP_NOT_AWAITING_APPROVAL" : "TOPUP_NOT_PENDING";
		return new ApiException(HttpStatus.CONFLICT, code, "The top-up is not in the expected state", null,
				Map.of("status", actual.name()));
	}

	/** The conditional flip: the row count says whether it was still in the expected state. */
	private void decide(UUID id, Status from, Status to, ZawedPrincipal actor, String reason, UUID transactionId) {
		int updated = jdbc.sql("""
				update topup_requests set status = :to, decided_by = :actor, decided_at = :now,
				       decision_reason = :reason, ledger_transaction_id = :txn, updated_at = :now
				where id = :id and status = :from
				""")
			.param("to", to.name())
			.param("from", from.name())
			.param("actor", actor.userId())
			.param("now", ts(clock.instant()))
			.param("reason", reason)
			.param("txn", transactionId)
			.param("id", id)
			.update();
		if (updated != 1) {
			throw notInStatus(from, to);
		}
	}

	private static ApiException referenceInUse() {
		return new ApiException(HttpStatus.CONFLICT, "BANK_REFERENCE_IN_USE",
				"A live top-up on this receiving account already carries this bank reference", "bankReference", null);
	}

	static AuditLog.Actor actorOf(ZawedPrincipal actor) {
		return new AuditLog.Actor(actor.userId(), actor.role());
	}

}
