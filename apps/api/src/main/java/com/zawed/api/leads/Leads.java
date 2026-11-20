package com.zawed.api.leads;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.UUID;

import io.micrometer.core.instrument.MeterRegistry;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.dao.DataAccessException;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.ratelimit.PostgresRateLimiter;
import com.zawed.api.platform.ratelimit.RateLimitedException;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * Demo requests (U1, u1-leads-contract.md). A request from the landing page becomes a lead in the Ops
 * console, unless it looks automated or repeats one from the last day.
 *
 * <p>Personal data: a lead's name, phone and e-mail never reach a log line, an audit row or an error.
 * Logs and audit rows carry the lead's id and reference only.
 *
 * <p>The public path has no principal, so its transaction has no scope; it writes through
 * {@code submit_lead} (V22), a {@code SECURITY DEFINER} function with fixed columns, and the runtime
 * role has no INSERT on {@code leads}. The Ops path runs in the signed-in user's platform scope, and
 * row-level security shows the rows to a platform user only.
 */
@Service
class Leads {

	private static final Logger LOG = LoggerFactory.getLogger(Leads.class);

	enum Status {

		NEW, CONTACTED, QUALIFIED, CLOSED

	}

	/** The form's values, cleaned and validated by the controller. */
	record Submission(String companyName, String contactName, String phone, String email, String fleetSize,
			String locale, String website, long startedAt) {
	}

	record Lead(UUID id, String reference, String companyName, String contactName, String phone, String email,
			String fleetSize, String locale, Status status, String note, Instant createdAt, Instant updatedAt) {

		static Lead map(ResultSet rs, int row) throws SQLException {
			return new Lead(rs.getObject("id", UUID.class), rs.getString("reference"), rs.getString("company_name"),
					rs.getString("contact_name"), rs.getString("phone"), rs.getString("email"), rs.getString("fleet_size"),
					rs.getString("locale"), Status.valueOf(rs.getString("status")), rs.getString("note"),
					instant(rs, "created_at"), instant(rs, "updated_at"));
		}

	}

	/** A person needs longer than this to fill in five fields; a script does not. */
	static final Duration MINIMUM_FILL_TIME = Duration.ofSeconds(3);

	/** A form shown longer ago than this is not a page someone has open. */
	static final Duration MAXIMUM_FORM_AGE = Duration.ofHours(24);

	/** Requests per phone and per e-mail in a day, whatever address they come from. */
	static final int PER_CONTACT_PER_DAY = 3;

	/** The metric every outcome is counted in, tagged {@code outcome}. */
	static final String METRIC = "zawed.leads.demo.requests";

	private final JdbcClient jdbc;
	private final TransactionTemplate transactions;
	private final PostgresRateLimiter limiter;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;
	private final MeterRegistry metrics;

	Leads(JdbcClient jdbc, TransactionTemplate transactions, PostgresRateLimiter limiter, AuditLog audit,
			IdGenerator ids, Clock clock, MeterRegistry metrics) {
		this.jdbc = jdbc;
		this.transactions = transactions;
		this.limiter = limiter;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
		this.metrics = metrics;
	}

	// ---------------------------------------------------------------------------------- public form

	/**
	 * Stores a demo request, counts it on a recent lead of the same phone or e-mail, or sets it aside.
	 * The caller answers the same in all three cases; only the per-contact limit refuses (429).
	 */
	void submit(Submission submission) {
		String dropped = dropReason(submission, clock.instant());
		if (dropped != null) {
			count(dropped);
			LOG.info("Demo request set aside ({})", dropped);
			return;
		}
		try {
			limiter.acquire("demo-phone-day", submission.phone(), PER_CONTACT_PER_DAY, Duration.ofDays(1));
			limiter.acquire("demo-email-day", submission.email(), PER_CONTACT_PER_DAY, Duration.ofDays(1));
		}
		catch (RateLimitedException ex) {
			count("rate_limited_contact");
			throw ex;
		}
		Stored stored = store(submission);
		count(stored.repeated() ? "repeated" : "created");
		LOG.info("Demo request {} as lead {} ({})", stored.repeated() ? "counted on" : "stored", stored.reference(),
				stored.id());
	}

	/**
	 * Why a request is set aside without a row, or null: the hidden field is filled, or the form was sent
	 * less than {@link #MINIMUM_FILL_TIME} after it was shown, before it was shown (a start time in the
	 * future), or more than {@link #MAXIMUM_FORM_AGE} after.
	 */
	static String dropReason(Submission submission, Instant now) {
		if (submission.website() != null && !submission.website().isBlank()) {
			return "honeypot";
		}
		Instant started = Instant.ofEpochMilli(submission.startedAt());
		if (started.isAfter(now)) {
			return "future";
		}
		if (started.isAfter(now.minus(MINIMUM_FILL_TIME))) {
			return "too_fast";
		}
		if (started.isBefore(now.minus(MAXIMUM_FORM_AGE))) {
			return "stale";
		}
		return null;
	}

	private record Stored(UUID id, String reference, boolean repeated) {
	}

	private Stored store(Submission s) {
		try {
			return transactions.execute((status) -> jdbc.sql("""
					select lead_id, lead_reference, repeated
					from submit_lead(:id, :company, :contact, :phone, :email, :fleetSize, :locale)
					""")
				.param("id", ids.next())
				.param("company", s.companyName())
				.param("contact", s.contactName())
				.param("phone", s.phone())
				.param("email", s.email())
				.param("fleetSize", s.fleetSize())
				.param("locale", s.locale())
				.query((rs, row) -> new Stored(rs.getObject(1, UUID.class), rs.getString(2), rs.getBoolean(3)))
				.single());
		}
		catch (DataAccessException ex) {
			// A database message can quote the failing row, which is the prospect's personal data: only the
			// SQL state is logged, and the cause is not passed on to the generic error handler's log line.
			Throwable cause = ex.getMostSpecificCause();
			LOG.error("Demo request could not be stored (SQL state {})",
					cause instanceof SQLException sql ? sql.getSQLState() : cause.getClass().getSimpleName());
			throw new IllegalStateException("The demo request could not be stored");
		}
	}

	/** Counts one outcome of the public form (see the contract for the list). */
	void count(String outcome) {
		metrics.counter(METRIC, "outcome", outcome).increment();
	}

	// ------------------------------------------------------------------------------------------ Ops

	/** Newest first, keyset on (created_at, id); the cursor is the last lead's id, as in every list. */
	@Transactional(readOnly = true)
	Page<Lead> list(Status status, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Lead> rows = jdbc.sql("""
				select * from leads
				where (cast(:status as text) is null or status = :status)
				  and (cast(:cursor as uuid) is null
				       or (created_at, id) < (select c.created_at, c.id from leads c where c.id = :cursor))
				order by created_at desc, id desc
				limit :limit
				""")
			.param("status", status == null ? null : status.name())
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Lead::map)
			.list();
		return Page.of(rows, size, Lead::id);
	}

	/**
	 * Sets the status and, when {@code note} is not null, the note (blank removes it). Closing starts the
	 * retention clock; reopening stops it. Audited in the same transaction, with the reference and the
	 * statuses only: never the note, which is free text about a person.
	 */
	@Transactional
	Lead update(ZawedPrincipal actor, UUID id, Status status, String note) {
		Lead before = jdbc.sql("select * from leads where id = :id for update")
			.param("id", id)
			.query(Lead::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Lead"));
		String newNote = note == null ? before.note() : DemoRequestText.note(note);
		Instant now = clock.instant();
		Lead after = jdbc.sql("""
				update leads
				set status = :status, note = :note, updated_at = :now, updated_by = :actor,
				    closed_at = case when :status = 'CLOSED' then coalesce(closed_at, :now) else null end
				where id = :id
				returning *
				""")
			.param("status", status.name())
			.param("note", newNote)
			.param("now", ts(now))
			.param("actor", actor.userId())
			.param("id", id)
			.query(Lead::map)
			.single();
		audit.record(new AuditLog.Actor(actor.userId(), actor.role()), "LEAD_UPDATED", "LEAD", id, Scope.PLATFORM,
				Map.of("reference", before.reference(), "from", before.status().name(), "to", status.name(), "noteChanged",
						!Objects.equals(before.note(), newNote)));
		return after;
	}

}
