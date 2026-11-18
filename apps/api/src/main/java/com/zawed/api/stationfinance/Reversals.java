package com.zawed.api.stationfinance;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;
import static com.zawed.api.stationfinance.BankAccounts.actorOf;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;

import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.ledger.Ledger;
import com.zawed.api.ledger.Ledger.Posted;
import com.zawed.api.ledger.Ledger.Poster;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * Two-person reversals (PRD §6.4, roadmap delta A4: "keep finality"). A completed fueling is never
 * edited, rejected or deleted. When it must be undone, one person asks with a reason and a
 * <b>different</b> person approves; approval posts a {@code REVERSAL} that is the exact mirror of
 * what the fueling posted:
 *
 * <ul>
 * <li>the company gets back what it paid: its wallet the part the wallet paid, its receivable the
 * part that was a debt (an offline fueling the wallet could not cover, B5);
 * <li>the station's payable is debited the full amount. If the fueling was already paid out, the
 * payable goes below zero: the station owes the difference, and it comes out of its next payouts
 * ({@link PayableBook}: nothing is available while the balance is negative).
 * </ul>
 *
 * <p>A fueling is reversed at most once: one live request per fueling here, one reversal per ledger
 * transaction in the ledger (V9). The fueling row does not change; the reversal is a record of its
 * own that both audiences can read once it is approved.
 *
 * <p>What a reversal does <b>not</b> do: it does not give back the vehicle's limit usage, and it does
 * not clear the tag's cooldown. Whether it should is a product decision that has not been made.
 */
@Service
class Reversals {

	enum Status {

		/** Requested, waiting for a second person. */
		PENDING,
		/** Approved and posted. */
		APPROVED,
		REJECTED

	}

	/** A reversal with the receipt reference and plate of the fueling it is about. */
	record Reversal(UUID id, UUID fuelingId, String fuelingReference, String plate, UUID companyId, UUID operatorId,
			UUID stationId, long amountPiastres, String reason, Status status, UUID requestedBy, Instant requestedAt,
			UUID decidedBy, Instant decidedAt, String decisionReason, UUID ledgerTransactionId) {

		static Reversal map(ResultSet rs, int row) throws SQLException {
			return new Reversal(rs.getObject("id", UUID.class), rs.getObject("fueling_id", UUID.class),
					rs.getString("fueling_reference"), rs.getString("plate"), rs.getObject("company_id", UUID.class),
					rs.getObject("operator_id", UUID.class), rs.getObject("station_id", UUID.class),
					rs.getLong("amount_piastres"), rs.getString("reason"), Status.valueOf(rs.getString("status")),
					rs.getObject("requested_by", UUID.class), instant(rs, "requested_at"),
					rs.getObject("decided_by", UUID.class), instant(rs, "decided_at"), rs.getString("decision_reason"),
					rs.getObject("ledger_transaction_id", UUID.class));
		}

	}

	/** List filters. A null field does not filter. */
	record Filter(UUID companyId, UUID operatorId, UUID stationId, UUID fuelingId, Status status) {
	}

	/** The facts of a fueling a reversal needs, read from the fueling's own row. */
	private record FuelingFacts(UUID id, String reference, String status, UUID companyId, UUID operatorId, UUID stationId,
			long amountPiastres, UUID ledgerTransactionId) {
	}

	private static final String SELECT = """
			select r.*, f.reference as fueling_reference, f.plate
			from fueling_reversals r join fuelings f on f.id = r.fueling_id
			""";

	private final JdbcClient jdbc;
	private final Ledger ledger;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;

	Reversals(JdbcClient jdbc, Ledger ledger, AuditLog audit, IdGenerator ids, Clock clock) {
		this.jdbc = jdbc;
		this.ledger = ledger;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
	}

	// ----------------------------------------------------------------------------------- queries

	@Transactional(readOnly = true)
	Page<Reversal> list(Filter filter, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Reversal> rows = jdbc.sql(SELECT + """
				where (cast(:company as uuid) is null or r.company_id = :company)
				  and (cast(:operator as uuid) is null or r.operator_id = :operator)
				  and (cast(:station as uuid) is null or r.station_id = :station)
				  and (cast(:fueling as uuid) is null or r.fueling_id = :fueling)
				  and (cast(:status as text) is null or r.status = :status)
				  and (cast(:cursor as uuid) is null or r.id < :cursor)
				order by r.id desc limit :limit
				""")
			.param("company", filter.companyId())
			.param("operator", filter.operatorId())
			.param("station", filter.stationId())
			.param("fueling", filter.fuelingId())
			.param("status", filter.status() == null ? null : filter.status().name())
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Reversal::map)
			.list();
		return Page.of(rows, size, Reversal::id);
	}

	@Transactional(readOnly = true)
	Reversal get(UUID id) {
		return jdbc.sql(SELECT + "where r.id = :id")
			.param("id", id)
			.query(Reversal::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Reversal"));
	}

	// ------------------------------------------------------------------------------ two persons

	/** Step one: ask for a completed fueling to be reversed, with the reason. Nothing moves yet. */
	@Transactional
	Reversal request(ZawedPrincipal actor, UUID fuelingId, String reason) {
		FuelingFacts fueling = fueling(fuelingId).orElseThrow(() -> ApiException.notFound("Fueling"));
		// Only a fueling that moved money can have that movement mirrored: a declined one posted
		// nothing, and neither did one that was not billed or cost nothing.
		if (!"COMPLETED".equals(fueling.status()) || fueling.ledgerTransactionId() == null) {
			throw new ApiException(HttpStatus.CONFLICT, "FUELING_NOT_REVERSIBLE",
					"Only a completed fueling that was charged can be reversed");
		}
		Optional<Status> live = liveStatus(fuelingId);
		if (live.isPresent()) {
			throw reversalExists(live.get());
		}
		UUID id = ids.next();
		Instant now = clock.instant();
		try {
			jdbc.sql("""
					insert into fueling_reversals (id, fueling_id, company_id, operator_id, station_id, amount_piastres,
					                               reason, status, requested_by, requested_at, created_at, updated_at)
					values (:id, :fueling, :company, :operator, :station, :amount, :reason, 'PENDING', :actor, :now, :now,
					        :now)
					""")
				.param("id", id)
				.param("fueling", fueling.id())
				.param("company", fueling.companyId())
				.param("operator", fueling.operatorId())
				.param("station", fueling.stationId())
				.param("amount", fueling.amountPiastres())
				.param("reason", reason)
				.param("actor", actor.userId())
				.param("now", ts(now))
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw reversalExists(Status.PENDING); // a concurrent request for the same fueling won the unique index
		}
		audit.record(actorOf(actor), "REVERSAL_REQUESTED", "FUELING_REVERSAL", id, Scope.company(fueling.companyId()),
				facts(fueling, reason));
		return get(id);
	}

	/**
	 * Step two, by a different person: the mirror of the fueling's posting is posted, in this
	 * transaction, and the request becomes {@code APPROVED}.
	 */
	@Transactional
	Reversal approve(ZawedPrincipal actor, UUID id) {
		Reversal reversal = lock(id);
		requirePending(reversal);
		if (reversal.requestedBy().equals(actor.userId())) {
			throw BankAccounts.selfApproval();
		}
		FuelingFacts fueling = fueling(reversal.fuelingId())
			.orElseThrow(() -> new IllegalStateException("Reversal " + id + " has no fueling"));
		// Lock order: the request row, then (inside the ledger) the company's wallet, then the
		// station's payable: the order a fueling takes them in. The ledger builds the mirror from the
		// fueling's own entries, so the wallet and receivable parts come back exactly as they went.
		Posted posted = ledger.reverse(new Ledger.Reversal(id, fueling.ledgerTransactionId(), fueling.reference(),
				"Reversal of fueling " + fueling.reference(), Poster.of(actor)));
		Instant now = clock.instant();
		int updated = jdbc.sql("""
				update fueling_reversals set status = 'APPROVED', decided_by = :actor, decided_at = :now,
				       ledger_transaction_id = :txn, updated_at = :now
				where id = :id and status = 'PENDING'
				""").param("actor", actor.userId()).param("now", ts(now)).param("txn", posted.transactionId()).param("id", id).update();
		if (updated != 1) {
			throw notPending(reversal.status());
		}
		Map<String, Object> facts = facts(fueling, reversal.reason());
		facts.put("requestedBy", reversal.requestedBy().toString());
		facts.put("transactionId", posted.transactionId().toString());
		if (!posted.wallets().isEmpty()) {
			facts.put("walletCreditedPiastres",
					posted.wallets().getFirst().balanceAfter() - posted.wallets().getFirst().balanceBefore());
			facts.put("walletAfterPiastres", posted.wallets().getFirst().balanceAfter());
		}
		// Negative here means the fueling had been paid out: the station now owes this much.
		facts.put("payableAfterPiastres", posted.payables().getFirst().balanceAfter());
		audit.record(actorOf(actor), "REVERSAL_APPROVED", "FUELING_REVERSAL", id, Scope.company(reversal.companyId()),
				facts);
		return get(id);
	}

	/** The request is refused, by anyone who may decide, the requester included. Nothing was posted. */
	@Transactional
	Reversal reject(ZawedPrincipal actor, UUID id, String reason) {
		Reversal reversal = lock(id);
		requirePending(reversal);
		Instant now = clock.instant();
		int updated = jdbc.sql("""
				update fueling_reversals set status = 'REJECTED', decided_by = :actor, decided_at = :now,
				       decision_reason = :reason, updated_at = :now
				where id = :id and status = 'PENDING'
				""").param("actor", actor.userId()).param("now", ts(now)).param("reason", reason).param("id", id).update();
		if (updated != 1) {
			throw notPending(reversal.status());
		}
		audit.record(actorOf(actor), "REVERSAL_REJECTED", "FUELING_REVERSAL", id, Scope.company(reversal.companyId()),
				Map.of("fuelingId", reversal.fuelingId().toString(), "reference", reversal.fuelingReference(),
						"amountPiastres", reversal.amountPiastres(), "reason", reason));
		return get(id);
	}

	// ----------------------------------------------------------------------------------- helpers

	private Optional<FuelingFacts> fueling(UUID fuelingId) {
		return jdbc.sql("""
				select id, reference, status, company_id, operator_id, station_id, amount_piastres, ledger_transaction_id
				from fuelings where id = :id
				""")
			.param("id", fuelingId)
			.query((rs, n) -> new FuelingFacts(rs.getObject("id", UUID.class), rs.getString("reference"),
					rs.getString("status"), rs.getObject("company_id", UUID.class), rs.getObject("operator_id", UUID.class),
					rs.getObject("station_id", UUID.class), rs.getLong("amount_piastres"),
					rs.getObject("ledger_transaction_id", UUID.class)))
			.optional();
	}

	/** The request that is pending or approved for this fueling, if there is one. */
	private Optional<Status> liveStatus(UUID fuelingId) {
		return jdbc.sql("select status from fueling_reversals where fueling_id = :fueling and status in ('PENDING', 'APPROVED')")
			.param("fueling", fuelingId)
			.query(String.class)
			.optional()
			.map(Status::valueOf);
	}

	/** Reads the request and holds its row until commit, so two decisions never both see PENDING. */
	private Reversal lock(UUID id) {
		// FOR UPDATE OF r: the fueling row is immutable and is not what is being decided.
		return jdbc.sql(SELECT + "where r.id = :id for update of r")
			.param("id", id)
			.query(Reversal::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Reversal"));
	}

	private static void requirePending(Reversal reversal) {
		if (reversal.status() != Status.PENDING) {
			throw notPending(reversal.status());
		}
	}

	private static Map<String, Object> facts(FuelingFacts fueling, String reason) {
		Map<String, Object> facts = new HashMap<>();
		facts.put("fuelingId", fueling.id().toString());
		facts.put("reference", fueling.reference());
		facts.put("stationId", fueling.stationId().toString());
		facts.put("amountPiastres", fueling.amountPiastres());
		facts.put("reason", reason);
		return facts;
	}

	private static ApiException notPending(Status status) {
		return new ApiException(HttpStatus.CONFLICT, "REVERSAL_NOT_PENDING", "The request has already been decided", null,
				Map.of("status", status.name()));
	}

	private static ApiException reversalExists(Status status) {
		return new ApiException(HttpStatus.CONFLICT, "REVERSAL_EXISTS",
				status == Status.APPROVED ? "This fueling has already been reversed"
						: "A reversal of this fueling is already waiting for approval",
				null, Map.of("status", status.name()));
	}

}
