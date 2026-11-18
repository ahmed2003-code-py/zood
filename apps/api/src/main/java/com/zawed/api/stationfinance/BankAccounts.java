package com.zawed.api.stationfinance;

import static com.zawed.api.platform.persistence.Sql.clean;
import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
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
import com.zawed.api.network.StationDirectory.StationRef;
import com.zawed.api.platform.crypto.DataEncryptor;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * Where a station is paid, under change control (roadmap delta A3: "changing the bank details is the
 * highest-value fraud path").
 *
 * <ul>
 * <li>A station has at most one verified ({@code ACTIVE}) account and at most one change waiting.
 * <li>A change is requested by one person, with the reference of the bank's letter, and approved by
 * a <b>different</b> person. The API refuses a self-approval and so does the database (V9).
 * <li>Approval starts a payout hold: for {@code zawed.finance.bank-change-hold-hours} (72 by
 * default) no payout may go to the station, so a fraudulent change has to survive three days of
 * people noticing before it can move money. The first account of a station is held like any other.
 * <li>The IBAN or account number is encrypted at rest, bound to its row, and never leaves this
 * class in clear: responses, the audit trail and logs carry its last four characters only.
 * </ul>
 */
@Service
class BankAccounts {

	enum Status {

		/** Requested, waiting for a second person. */
		PENDING,
		/** The station's verified account. */
		ACTIVE,
		REJECTED,
		/** Was the verified account until a later change replaced it. */
		SUPERSEDED

	}

	enum AccountKind {

		IBAN, ACCOUNT_NUMBER

	}

	/** An account as anyone may see it: the number itself is not part of the record. */
	record BankAccount(UUID id, UUID stationId, UUID operatorId, AccountKind accountKind, String accountLast4,
			String bankName, String holderName, String bankLetterReference, Status status, UUID requestedBy,
			Instant requestedAt, UUID decidedBy, Instant decidedAt, String decisionReason, Instant payoutsHeldUntil,
			Instant supersededAt) {

		static BankAccount map(ResultSet rs, int row) throws SQLException {
			return new BankAccount(rs.getObject("id", UUID.class), rs.getObject("station_id", UUID.class),
					rs.getObject("operator_id", UUID.class), AccountKind.valueOf(rs.getString("account_kind")),
					rs.getString("account_last4"), rs.getString("bank_name"), rs.getString("holder_name"),
					rs.getString("bank_letter_reference"), Status.valueOf(rs.getString("status")),
					rs.getObject("requested_by", UUID.class), instant(rs, "requested_at"),
					rs.getObject("decided_by", UUID.class), instant(rs, "decided_at"), rs.getString("decision_reason"),
					instant(rs, "payouts_held_until"), instant(rs, "superseded_at"));
		}

	}

	/** What a station's bank page shows: the verified account and a change that waits; either may be null. */
	record Details(BankAccount active, BankAccount pending) {
	}

	/** An account number as typed, reduced to the form that is validated, encrypted and compared. */
	record ParsedAccount(AccountKind kind, String normalized) {

		String last4() {
			return normalized.substring(normalized.length() - 4);
		}

		@Override
		public String toString() {
			return "ParsedAccount[" + kind + ", ****" + last4() + "]"; // never the number
		}

	}

	/** Every column except the encrypted number, which no read needs. */
	private static final String COLUMNS = """
			id, station_id, operator_id, account_kind, account_last4, bank_name, holder_name, bank_letter_reference,
			status, requested_by, requested_at, decided_by, decided_at, decision_reason, payouts_held_until,
			superseded_at""";

	private final JdbcClient jdbc;
	private final StationAccess access;
	private final Ledger ledger;
	private final DataEncryptor encryptor;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;
	private final Duration hold;

	BankAccounts(JdbcClient jdbc, StationAccess access, Ledger ledger, DataEncryptor encryptor, AuditLog audit,
			IdGenerator ids, Clock clock, @Value("${zawed.finance.bank-change-hold-hours:72}") long holdHours) {
		if (holdHours < 0) {
			throw new IllegalArgumentException("zawed.finance.bank-change-hold-hours must not be negative: " + holdHours);
		}
		this.jdbc = jdbc;
		this.access = access;
		this.ledger = ledger;
		this.encryptor = encryptor;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
		this.hold = Duration.ofHours(holdHours);
	}

	// ----------------------------------------------------------------------------------- queries

	@Transactional(readOnly = true)
	Details details(ZawedPrincipal principal, UUID stationId) {
		access.require(principal, stationId);
		return new Details(byStatus(stationId, Status.ACTIVE).orElse(null), byStatus(stationId, Status.PENDING).orElse(null));
	}

	/** The change queue and its history, newest first. */
	@Transactional(readOnly = true)
	Page<BankAccount> changes(Status status, UUID stationId, UUID operatorId, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<BankAccount> rows = jdbc.sql("select " + COLUMNS + """
				 from station_bank_accounts
				where (cast(:status as text) is null or status = :status)
				  and (cast(:station as uuid) is null or station_id = :station)
				  and (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("status", status == null ? null : status.name())
			.param("station", stationId)
			.param("operator", operatorId)
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(BankAccount::map)
			.list();
		return Page.of(rows, size, BankAccount::id);
	}

	/** The station's verified account, if it has one. Runs in the caller's transaction. */
	Optional<BankAccount> active(UUID stationId) {
		return byStatus(stationId, Status.ACTIVE);
	}

	// ---------------------------------------------------------------------------- change control

	/** Step one: Operations asks for the station to be paid into this account from now on. */
	@Transactional
	BankAccount requestChange(ZawedPrincipal actor, UUID stationId, String accountNumber, String bankName,
			String holderName, String bankLetterReference) {
		StationRef station = access.require(actor, stationId);
		ParsedAccount account = parse(accountNumber);
		if (byStatus(stationId, Status.PENDING).isPresent()) {
			throw changePending();
		}
		UUID id = ids.next();
		Instant now = clock.instant();
		try {
			jdbc.sql("""
					insert into station_bank_accounts (id, station_id, operator_id, account_kind, account_encrypted,
					                                   account_last4, bank_name, holder_name, bank_letter_reference, status,
					                                   requested_by, requested_at, created_at, updated_at)
					values (:id, :station, :operator, :kind, :encrypted, :last4, :bank, :holder, :letter, 'PENDING', :actor,
					        :now, :now, :now)
					""")
				.param("id", id)
				.param("station", station.id())
				.param("operator", station.operatorId())
				.param("kind", account.kind().name())
				.param("encrypted", encryptor.encrypt(account.normalized().getBytes(StandardCharsets.UTF_8), binding(id)))
				.param("last4", account.last4())
				.param("bank", clean(bankName))
				.param("holder", clean(holderName))
				.param("letter", clean(bankLetterReference))
				.param("actor", actor.userId())
				.param("now", ts(now))
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw changePending(); // a concurrent request for the same station won the unique index
		}
		audit.record(actorOf(actor), "BANK_ACCOUNT_CHANGE_REQUESTED", "STATION_BANK_ACCOUNT", id,
				Scope.operator(station.operatorId()),
				Map.of("stationId", station.id().toString(), "accountKind", account.kind().name(), "accountLast4",
						account.last4(), "bankName", clean(bankName), "bankLetterReference", clean(bankLetterReference)));
		return get(id);
	}

	/**
	 * Step two, by a different person: the account becomes the station's verified one, the previous
	 * one is superseded, and the payout hold starts.
	 */
	@Transactional
	BankAccount approve(ZawedPrincipal actor, UUID id) {
		BankAccount change = lock(id);
		requirePending(change);
		if (change.requestedBy().equals(actor.userId())) {
			throw selfApproval();
		}
		// The payable lock is what a payout to this station holds while it checks the account and the
		// hold (Payouts.record, Payouts.approve). Taking it here puts the two in one order: a payout
		// either committed before this change, to the account that was verified then, or starts after
		// it and meets the hold. Lock order: the change row, then the payable; a payout never locks a
		// bank account row, so the two cannot wait on each other.
		ledger.lockPayable(change.stationId());
		Instant now = clock.instant();
		Instant heldUntil = now.plus(hold);
		Optional<UUID> replaced = jdbc.sql("""
				update station_bank_accounts set status = 'SUPERSEDED', superseded_at = :now, updated_at = :now
				where station_id = :station and status = 'ACTIVE' returning id
				""").param("station", change.stationId()).param("now", ts(now)).query(UUID.class).optional();
		int updated = jdbc.sql("""
				update station_bank_accounts set status = 'ACTIVE', decided_by = :actor, decided_at = :now,
				       payouts_held_until = :heldUntil, updated_at = :now
				where id = :id and status = 'PENDING'
				""").param("actor", actor.userId()).param("now", ts(now)).param("heldUntil", ts(heldUntil)).param("id", id).update();
		if (updated != 1) {
			throw notPending(change.status());
		}
		Map<String, Object> facts = new HashMap<>();
		facts.put("stationId", change.stationId().toString());
		facts.put("accountLast4", change.accountLast4());
		facts.put("requestedBy", change.requestedBy().toString());
		facts.put("payoutsHeldUntil", heldUntil.toString());
		replaced.ifPresent((previous) -> facts.put("replacedAccountId", previous.toString()));
		audit.record(actorOf(actor), "BANK_ACCOUNT_CHANGE_APPROVED", "STATION_BANK_ACCOUNT", id,
				Scope.operator(change.operatorId()), facts);
		return get(id);
	}

	/** The change is refused, by anyone who may decide, the requester included: nothing becomes payable. */
	@Transactional
	BankAccount reject(ZawedPrincipal actor, UUID id, String reason) {
		BankAccount change = lock(id);
		requirePending(change);
		Instant now = clock.instant();
		int updated = jdbc.sql("""
				update station_bank_accounts set status = 'REJECTED', decided_by = :actor, decided_at = :now,
				       decision_reason = :reason, updated_at = :now
				where id = :id and status = 'PENDING'
				""").param("actor", actor.userId()).param("now", ts(now)).param("reason", reason).param("id", id).update();
		if (updated != 1) {
			throw notPending(change.status());
		}
		audit.record(actorOf(actor), "BANK_ACCOUNT_CHANGE_REJECTED", "STATION_BANK_ACCOUNT", id,
				Scope.operator(change.operatorId()), Map.of("stationId", change.stationId().toString(), "accountLast4",
						change.accountLast4(), "reason", reason));
		return get(id);
	}

	// ----------------------------------------------------------------------------------- helpers

	@Transactional(readOnly = true)
	BankAccount get(UUID id) {
		return jdbc.sql("select " + COLUMNS + " from station_bank_accounts where id = :id")
			.param("id", id)
			.query(BankAccount::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Bank account change"));
	}

	private Optional<BankAccount> byStatus(UUID stationId, Status status) {
		return jdbc.sql("select " + COLUMNS + " from station_bank_accounts where station_id = :station and status = :status")
			.param("station", stationId)
			.param("status", status.name())
			.query(BankAccount::map)
			.optional();
	}

	/** Reads the change and holds its row until commit, so two decisions never both see PENDING. */
	private BankAccount lock(UUID id) {
		return jdbc.sql("select " + COLUMNS + " from station_bank_accounts where id = :id for update")
			.param("id", id)
			.query(BankAccount::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Bank account change"));
	}

	private static void requirePending(BankAccount change) {
		if (change.status() != Status.PENDING) {
			throw notPending(change.status());
		}
	}

	/**
	 * Recognises and checks what was typed. Spaces, dashes and invisible marks are dropped and digits
	 * of any script folded, exactly as for a bank reference. Two leading letters mean an IBAN, which
	 * must have the standard shape and pass its mod-97 check (an Egyptian one is 29 characters);
	 * anything else must be a plain account number of 6 to 34 digits.
	 *
	 * @throws ApiException 400 {@code INVALID_BANK_ACCOUNT}; the message never repeats the input
	 */
	static ParsedAccount parse(String accountNumber) {
		String value = BankReferences.key(accountNumber == null ? "" : accountNumber);
		if (value.length() >= 2 && isLetter(value.charAt(0)) && isLetter(value.charAt(1))) {
			boolean shaped = value.length() >= 15 && value.length() <= 34 && isDigit(value.charAt(2))
					&& isDigit(value.charAt(3)) && (!value.startsWith("EG") || value.length() == 29);
			if (!shaped || mod97(value.substring(4) + value.substring(0, 4)) != 1) {
				throw invalidAccount();
			}
			return new ParsedAccount(AccountKind.IBAN, value);
		}
		if (value.length() < 6 || value.length() > 34 || !value.chars().allMatch((c) -> isDigit((char) c))) {
			throw invalidAccount();
		}
		return new ParsedAccount(AccountKind.ACCOUNT_NUMBER, value);
	}

	/** ISO 13616: letters count as 10 to 35; the number is far too long for a long, so it is reduced as it is read. */
	private static int mod97(String rearranged) {
		int remainder = 0;
		for (int i = 0; i < rearranged.length(); i++) {
			char c = rearranged.charAt(i);
			remainder = isDigit(c) ? (remainder * 10 + (c - '0')) % 97 : (remainder * 100 + (c - 'A' + 10)) % 97;
		}
		return remainder;
	}

	private static boolean isLetter(char c) {
		return c >= 'A' && c <= 'Z';
	}

	private static boolean isDigit(char c) {
		return c >= '0' && c <= '9';
	}

	/** Binds the encrypted number to its row: copied into another row, it does not decrypt. */
	static byte[] binding(UUID accountId) {
		return ByteBuffer.allocate(20)
			.put("sbac".getBytes(StandardCharsets.US_ASCII))
			.putLong(accountId.getMostSignificantBits())
			.putLong(accountId.getLeastSignificantBits())
			.array();
	}

	private static ApiException invalidAccount() {
		return new ApiException(HttpStatus.BAD_REQUEST, "INVALID_BANK_ACCOUNT",
				"Enter a valid IBAN, or an account number of 6 to 34 digits", "accountNumber", null);
	}

	private static ApiException changePending() {
		return new ApiException(HttpStatus.CONFLICT, "BANK_ACCOUNT_CHANGE_PENDING",
				"A bank account change for this station is already waiting for approval");
	}

	private static ApiException notPending(Status status) {
		return new ApiException(HttpStatus.CONFLICT, "BANK_ACCOUNT_CHANGE_NOT_PENDING",
				"The change has already been decided", null, Map.of("status", status.name()));
	}

	static ApiException selfApproval() {
		return new ApiException(HttpStatus.FORBIDDEN, "SELF_APPROVAL_NOT_ALLOWED",
				"A second person must approve this; you cannot approve your own request");
	}

	static AuditLog.Actor actorOf(ZawedPrincipal actor) {
		return new AuditLog.Actor(actor.userId(), actor.role());
	}

}
