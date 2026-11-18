package com.zawed.api.stationfinance;

import static com.zawed.api.platform.persistence.Sql.clean;
import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;
import static com.zawed.api.stationfinance.BankAccounts.actorOf;

import java.sql.Date;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import org.springframework.beans.factory.annotation.Value;
import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.ledger.BankReferences;
import com.zawed.api.ledger.Ledger;
import com.zawed.api.ledger.Ledger.Account;
import com.zawed.api.ledger.Ledger.Entry;
import com.zawed.api.ledger.Ledger.Posted;
import com.zawed.api.ledger.Ledger.Poster;
import com.zawed.api.ledger.Ledger.Posting;
import com.zawed.api.ledger.Ledger.SourceType;
import com.zawed.api.network.StationDirectory.StationRef;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;
import com.zawed.api.stationfinance.BankAccounts.BankAccount;
import com.zawed.api.stationfinance.PayableBook.Position;

/**
 * Payouts to stations (roadmap delta A1; plan §3.5: station payable → {@code platform:bank}).
 * Operations records the bank transfer it made: amount, the bank's reference, the value date.
 *
 * <ul>
 * <li><b>Never more than is there.</b> Under the station's payable lock, the amount must fit in
 * {@code available − payouts waiting for approval} ({@link PayableBook}). Two payouts recorded at
 * the same moment are decided one after the other.
 * <li><b>Dual control.</b> Up to {@code zawed.finance.payout-approval-threshold-piastres}
 * (5,000,000, i.e. 50,000 EGP, by default) a payout posts at once. Above it, it waits as
 * {@code PENDING}, reserving its amount, until a <b>different</b> person approves it; approval
 * checks everything again and only then posts.
 * <li><b>Only to a verified account, outside its hold.</b> The station needs an {@code ACTIVE} bank
 * account whose payout hold has passed ({@link BankAccounts}); a payout that waited while the
 * account changed cannot be approved any more.
 * <li><b>One transfer pays once.</b> The reference, normalised like a top-up's, belongs to at most
 * one pending or posted payout platform-wide; a cancelled payout frees it. The posting's source is
 * the payout id, so a payout can never post twice either.
 * </ul>
 */
@Service
class Payouts {

	enum Status {

		/** Above the threshold, waiting for a second person. Its amount is reserved. */
		PENDING,
		/** Posted to the ledger: the station's payable is debited. */
		POSTED,
		CANCELLED

	}

	record Payout(UUID id, UUID stationId, UUID operatorId, UUID bankAccountId, long amountPiastres,
			String bankReference, LocalDate valueDate, String note, Status status, boolean approvalRequired,
			long approvalThresholdPiastres, UUID requestedBy, Instant requestedAt, UUID decidedBy, Instant decidedAt,
			String decisionReason, Instant postedAt, UUID ledgerTransactionId) {

		static Payout map(ResultSet rs, int row) throws SQLException {
			return new Payout(rs.getObject("id", UUID.class), rs.getObject("station_id", UUID.class),
					rs.getObject("operator_id", UUID.class), rs.getObject("bank_account_id", UUID.class),
					rs.getLong("amount_piastres"), rs.getString("bank_reference"), rs.getDate("value_date").toLocalDate(),
					rs.getString("note"), Status.valueOf(rs.getString("status")), rs.getBoolean("approval_required"),
					rs.getLong("approval_threshold_piastres"), rs.getObject("requested_by", UUID.class),
					instant(rs, "requested_at"), rs.getObject("decided_by", UUID.class), instant(rs, "decided_at"),
					rs.getString("decision_reason"), instant(rs, "posted_at"),
					rs.getObject("ledger_transaction_id", UUID.class));
		}

	}

	/** List filters. A null field does not filter. */
	record Filter(UUID operatorId, UUID stationId, Status status) {
	}

	/** A sanity cap on one payout: 1,000,000,000 EGP. Above it is a typo, not a transfer. */
	static final long MAX_AMOUNT_PIASTRES = 100_000_000_000L;

	/** Value dates are business dates in Egypt (PRD §2.3). */
	static final ZoneId BUSINESS_ZONE = ZoneId.of("Africa/Cairo");

	/** A transfer ordered today may carry a value date a few days ahead; further is a typing error. */
	static final int MAX_VALUE_DATE_DAYS_AHEAD = 7;

	private final JdbcClient jdbc;
	private final StationAccess access;
	private final BankAccounts bankAccounts;
	private final PayableBook book;
	private final Ledger ledger;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;
	private final long approvalThreshold;

	Payouts(JdbcClient jdbc, StationAccess access, BankAccounts bankAccounts, PayableBook book, Ledger ledger,
			AuditLog audit, IdGenerator ids, Clock clock,
			@Value("${zawed.finance.payout-approval-threshold-piastres:5000000}") long approvalThreshold) {
		if (approvalThreshold < 0) {
			throw new IllegalArgumentException(
					"zawed.finance.payout-approval-threshold-piastres must not be negative: " + approvalThreshold);
		}
		this.jdbc = jdbc;
		this.access = access;
		this.bankAccounts = bankAccounts;
		this.book = book;
		this.ledger = ledger;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
		this.approvalThreshold = approvalThreshold;
	}

	// ----------------------------------------------------------------------------------- queries

	@Transactional(readOnly = true)
	Page<Payout> list(Filter filter, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Payout> rows = jdbc.sql("""
				select * from payouts
				where (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:station as uuid) is null or station_id = :station)
				  and (cast(:status as text) is null or status = :status)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("operator", filter.operatorId())
			.param("station", filter.stationId())
			.param("status", filter.status() == null ? null : filter.status().name())
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Payout::map)
			.list();
		return Page.of(rows, size, Payout::id);
	}

	@Transactional(readOnly = true)
	Payout get(UUID id) {
		return jdbc.sql("select * from payouts where id = :id")
			.param("id", id)
			.query(Payout::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Payout"));
	}

	// ----------------------------------------------------------------------------------- record

	/**
	 * Records a payout. At or below the threshold it posts now; above it, it is stored as
	 * {@code PENDING} for a second person.
	 */
	@Transactional
	Payout record(ZawedPrincipal actor, UUID stationId, long amountPiastres, String bankReference, LocalDate valueDate,
			String note) {
		String reference = BankReferences.clean(bankReference);
		checkValueDate(valueDate);
		StationRef station = access.require(actor, stationId);

		// Everything below is decided under the station's payable lock.
		ledger.lockPayable(station.id());
		BankAccount account = payableAccount(station.id());
		requireAvailable(book.position(station.id()), amountPiastres, 0);
		// Asked before the insert so the common duplicate is a clean refusal; the unique index on the
		// normalised reference is the final word under a race.
		if (referenceInUse(reference)) {
			throw referenceTaken();
		}

		UUID id = ids.next();
		Instant now = clock.instant();
		boolean approvalRequired = amountPiastres > approvalThreshold;
		UUID transactionId = null;
		Long balanceAfter = null;
		if (!approvalRequired) {
			Posted posted = post(actor, id, station.id(), amountPiastres, reference);
			transactionId = posted.transactionId();
			balanceAfter = posted.payables().getFirst().balanceAfter();
		}
		try {
			jdbc.sql("""
					insert into payouts (id, station_id, operator_id, bank_account_id, amount_piastres, bank_reference,
					                     bank_reference_key, value_date, note, status, approval_threshold_piastres,
					                     approval_required, requested_by, requested_at, posted_at, ledger_transaction_id,
					                     created_at, updated_at)
					values (:id, :station, :operator, :account, :amount, :reference, :key, :valueDate, :note, :status,
					        :threshold, :approvalRequired, :actor, :now, :postedAt, :txn, :now, :now)
					""")
				.param("id", id)
				.param("station", station.id())
				.param("operator", station.operatorId())
				.param("account", account.id())
				.param("amount", amountPiastres)
				.param("reference", reference)
				.param("key", BankReferences.key(reference))
				.param("valueDate", Date.valueOf(valueDate))
				.param("note", clean(note))
				.param("status", (approvalRequired ? Status.PENDING : Status.POSTED).name())
				.param("threshold", approvalThreshold)
				.param("approvalRequired", approvalRequired)
				.param("actor", actor.userId())
				.param("now", ts(now))
				.param("postedAt", approvalRequired ? null : ts(now))
				.param("txn", transactionId)
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw referenceTaken(); // rolls a posting back with it
		}
		Map<String, Object> facts = facts(station.id(), amountPiastres, reference, account);
		facts.put("valueDate", valueDate.toString());
		facts.put("approvalRequired", approvalRequired);
		if (transactionId != null) {
			facts.put("transactionId", transactionId.toString());
			facts.put("payableAfterPiastres", balanceAfter);
		}
		audit.record(actorOf(actor), approvalRequired ? "PAYOUT_RECORDED" : "PAYOUT_POSTED", "PAYOUT", id,
				Scope.operator(station.operatorId()), facts);
		return get(id);
	}

	// ---------------------------------------------------------------------------------- decide

	/**
	 * The second person's word on a payout above the threshold. Nothing the first check found is
	 * taken on trust: the account, its hold and what is available are read again under the lock.
	 */
	@Transactional
	Payout approve(ZawedPrincipal actor, UUID id) {
		Payout payout = lock(id);
		requirePending(payout);
		if (payout.requestedBy().equals(actor.userId())) {
			throw BankAccounts.selfApproval();
		}
		// Lock order: the payout row, then the payable. Recording a payout takes the payable and then
		// inserts a new row, which nobody else can hold, so the two cannot wait on each other.
		ledger.lockPayable(payout.stationId());
		BankAccount account = payableAccount(payout.stationId());
		if (!account.id().equals(payout.bankAccountId())) {
			throw new ApiException(HttpStatus.CONFLICT, "PAYOUT_BANK_ACCOUNT_CHANGED",
					"The station's bank account changed after this payout was recorded; cancel it and record it again");
		}
		// This payout is itself among the pending ones; it does not compete with itself.
		requireAvailable(book.position(payout.stationId()), payout.amountPiastres(), payout.amountPiastres());

		Posted posted = post(actor, id, payout.stationId(), payout.amountPiastres(), payout.bankReference());
		Instant now = clock.instant();
		int updated = jdbc.sql("""
				update payouts set status = 'POSTED', decided_by = :actor, decided_at = :now, posted_at = :now,
				       ledger_transaction_id = :txn, updated_at = :now
				where id = :id and status = 'PENDING'
				""").param("actor", actor.userId()).param("now", ts(now)).param("txn", posted.transactionId()).param("id", id).update();
		if (updated != 1) {
			throw notPending(payout.status());
		}
		Map<String, Object> facts = facts(payout.stationId(), payout.amountPiastres(), payout.bankReference(), account);
		facts.put("requestedBy", payout.requestedBy().toString());
		facts.put("transactionId", posted.transactionId().toString());
		facts.put("payableAfterPiastres", posted.payables().getFirst().balanceAfter());
		audit.record(actorOf(actor), "PAYOUT_APPROVED", "PAYOUT", id, Scope.operator(payout.operatorId()), facts);
		return get(id);
	}

	/** Withdraws a payout that still waits. Nothing was posted, so nothing is undone; its reference is free again. */
	@Transactional
	Payout cancel(ZawedPrincipal actor, UUID id, String reason) {
		Payout payout = lock(id);
		requirePending(payout);
		Instant now = clock.instant();
		int updated = jdbc.sql("""
				update payouts set status = 'CANCELLED', decided_by = :actor, decided_at = :now, decision_reason = :reason,
				       updated_at = :now
				where id = :id and status = 'PENDING'
				""").param("actor", actor.userId()).param("now", ts(now)).param("reason", reason).param("id", id).update();
		if (updated != 1) {
			throw notPending(payout.status());
		}
		audit.record(actorOf(actor), "PAYOUT_CANCELLED", "PAYOUT", id, Scope.operator(payout.operatorId()),
				Map.of("stationId", payout.stationId().toString(), "amountPiastres", payout.amountPiastres(),
						"bankReference", payout.bankReference(), "reason", reason));
		return get(id);
	}

	// ----------------------------------------------------------------------------------- helpers

	/** Station payable (debit) to the platform's bank (credit); the source is the payout, which posts once. */
	private Posted post(ZawedPrincipal actor, UUID payoutId, UUID stationId, long amountPiastres, String reference) {
		return ledger.post(new Posting(SourceType.PAYOUT, payoutId, reference, "Payout by bank transfer",
				Poster.of(actor), List.of(Entry.debit(Account.stationPayable(stationId), amountPiastres),
						Entry.credit(Account.platformBank(), amountPiastres))));
	}

	/** The verified account a payout may go to now: it exists and its hold has passed. */
	private BankAccount payableAccount(UUID stationId) {
		BankAccount account = bankAccounts.active(stationId)
			.orElseThrow(() -> new ApiException(HttpStatus.CONFLICT, "BANK_ACCOUNT_REQUIRED",
					"The station has no verified bank account"));
		if (clock.instant().isBefore(account.payoutsHeldUntil())) {
			throw new ApiException(HttpStatus.CONFLICT, "PAYOUT_ON_HOLD",
					"Payouts to this station are on hold after its bank account changed", null,
					Map.of("heldUntil", account.payoutsHeldUntil().toString()));
		}
		return account;
	}

	/**
	 * @param ownPending what this payout already contributes to the pending total (its own amount
	 * when it is being approved, nothing when it is being recorded)
	 */
	private static void requireAvailable(Position position, long amountPiastres, long ownPending) {
		long payable = Math.addExact(position.payablePiastres(), ownPending);
		if (amountPiastres > payable) {
			throw new ApiException(HttpStatus.CONFLICT, "PAYOUT_EXCEEDS_AVAILABLE",
					"The payout is more than the station's available payable", "amountPiastres",
					Map.of("availablePiastres", position.availablePiastres(), "pendingPayoutsPiastres",
							position.pendingPayoutsPiastres() - ownPending, "requestedPiastres", amountPiastres));
		}
	}

	private boolean referenceInUse(String reference) {
		return Boolean.TRUE.equals(jdbc.sql("""
				select exists (select 1 from payouts where bank_reference_key = :key and status in ('PENDING', 'POSTED'))
				""").param("key", BankReferences.key(reference)).query(Boolean.class).single());
	}

	private void checkValueDate(LocalDate valueDate) {
		LocalDate today = LocalDate.ofInstant(clock.instant(), BUSINESS_ZONE);
		if (valueDate.isAfter(today.plusDays(MAX_VALUE_DATE_DAYS_AHEAD))) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "VALUE_DATE_TOO_FAR",
					"The value date is more than " + MAX_VALUE_DATE_DAYS_AHEAD + " days ahead", "valueDate",
					Map.of("maxDaysAhead", MAX_VALUE_DATE_DAYS_AHEAD));
		}
	}

	/** Reads the payout and holds its row until commit, so two decisions never both see PENDING. */
	private Payout lock(UUID id) {
		return jdbc.sql("select * from payouts where id = :id for update")
			.param("id", id)
			.query(Payout::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Payout"));
	}

	private static void requirePending(Payout payout) {
		if (payout.status() != Status.PENDING) {
			throw notPending(payout.status());
		}
	}

	private static Map<String, Object> facts(UUID stationId, long amountPiastres, String reference, BankAccount account) {
		Map<String, Object> facts = new HashMap<>();
		facts.put("stationId", stationId.toString());
		facts.put("amountPiastres", amountPiastres);
		facts.put("bankReference", reference);
		facts.put("bankAccountId", account.id().toString());
		facts.put("accountLast4", account.accountLast4());
		return facts;
	}

	private static ApiException notPending(Status status) {
		return new ApiException(HttpStatus.CONFLICT, "PAYOUT_NOT_PENDING", "The payout has already been decided", null,
				Map.of("status", status.name()));
	}

	private static ApiException referenceTaken() {
		return new ApiException(HttpStatus.CONFLICT, "BANK_REFERENCE_IN_USE",
				"A pending or posted payout already carries this bank reference", "bankReference", null);
	}

}
