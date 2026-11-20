package com.zawed.api.tickets;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.EnumSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;

import org.springframework.context.ApplicationEventPublisher;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * Support tickets (roadmap delta A5, plan §7 B6). A company or a station asks Operations for
 * something; the thread is the record of what was asked and answered. Master data still changes
 * only at Operations: a ticket is the request, not the change. Runs in the caller's scope, so
 * row-level security shows each side only its own tickets, and never Operations' internal notes.
 */
@Service
class Tickets {

	enum Type {

		LOST_TAG, STOLEN_TAG, LIMIT_CHANGE, VEHICLE_CHANGE, TOPUP_QUERY, DEVICE_FAULT, TAG_READ_ISSUE, PAYOUT_QUERY,
		PRICE_QUERY, DISPUTE, OTHER

	}

	enum Status {

		OPEN, IN_PROGRESS, RESOLVED, CLOSED

	}

	/** What each side may ask about. */
	static final Set<Type> COMPANY_TYPES = EnumSet.of(Type.LOST_TAG, Type.STOLEN_TAG, Type.LIMIT_CHANGE,
			Type.VEHICLE_CHANGE, Type.TOPUP_QUERY, Type.DISPUTE, Type.OTHER);

	static final Set<Type> STATION_TYPES = EnumSet.of(Type.DEVICE_FAULT, Type.TAG_READ_ISSUE, Type.PAYOUT_QUERY,
			Type.PRICE_QUERY, Type.DISPUTE, Type.OTHER);

	record Ticket(UUID id, String reference, ScopeType scopeType, UUID companyId, UUID operatorId, Type type,
			String subject, Status status, UUID vehicleId, UUID stationId, UUID fuelingId, UUID createdBy,
			Instant createdAt, Instant updatedAt, UUID assignedTo, Instant resolvedAt) {

		static Ticket map(ResultSet rs, int row) throws SQLException {
			return new Ticket(rs.getObject("id", UUID.class), rs.getString("reference"),
					ScopeType.valueOf(rs.getString("scope_type")), rs.getObject("company_id", UUID.class),
					rs.getObject("operator_id", UUID.class), Type.valueOf(rs.getString("type")), rs.getString("subject"),
					Status.valueOf(rs.getString("status")), rs.getObject("vehicle_id", UUID.class),
					rs.getObject("station_id", UUID.class), rs.getObject("fueling_id", UUID.class),
					rs.getObject("created_by", UUID.class), instant(rs, "created_at"), instant(rs, "updated_at"),
					rs.getObject("assigned_to", UUID.class), instant(rs, "resolved_at"));
		}

	}

	record Message(UUID id, UUID authorId, String authorRole, String body, boolean internal, Instant createdAt) {

		static Message map(ResultSet rs, int row) throws SQLException {
			return new Message(rs.getObject("id", UUID.class), rs.getObject("author_id", UUID.class),
					rs.getString("author_role"), rs.getString("body"), rs.getBoolean("internal"), instant(rs, "created_at"));
		}

	}

	record TicketThread(Ticket ticket, List<Message> messages) {
	}

	record Draft(Type type, String subject, String body, UUID vehicleId, UUID stationId, UUID fuelingId) {
	}

	private final JdbcClient jdbc;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;
	private final ApplicationEventPublisher events;

	Tickets(JdbcClient jdbc, AuditLog audit, IdGenerator ids, Clock clock, ApplicationEventPublisher events) {
		this.jdbc = jdbc;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
		this.events = events;
	}

	/** A company or station opens a ticket with its first message. */
	@Transactional
	TicketThread open(ZawedPrincipal actor, Draft draft) {
		Scope scope = actor.scope();
		boolean company = scope.type() == ScopeType.COMPANY;
		if (scope.type() != ScopeType.COMPANY && scope.type() != ScopeType.OPERATOR) {
			throw new ApiException(HttpStatus.FORBIDDEN, "FORBIDDEN", "Operations answers tickets; it does not open them");
		}
		Set<Type> allowed = company ? COMPANY_TYPES : STATION_TYPES;
		if (!allowed.contains(draft.type())) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "TICKET_TYPE_NOT_ALLOWED", "This kind of request is not available here",
					"type", null);
		}
		// A foreign key is checked without row-level security, so a ticket could otherwise point at
		// another tenant's vehicle, station or fueling. Each link must be a row the caller can see
		// (B6 contract). That holds for a company's station link too (B7 W2 IDOR sweep): it used to be
		// skipped, so a company could link any operator's station, and an id that did not exist failed
		// the foreign key with a 500, which told real stations from made-up ones. A company sees no
		// stations, so it links the fueling instead, which names its station.
		requireVisible("vehicles", draft.vehicleId(), "vehicleId");
		requireVisible("fuelings", draft.fuelingId(), "fuelingId");
		requireVisible("stations", draft.stationId(), "stationId");
		Instant now = clock.instant();
		UUID id = ids.next();
		String reference = "T-" + jdbc.sql("select nextval('ticket_reference_seq')").query(Long.class).single();
		jdbc.sql("""
				insert into tickets (id, reference, scope_type, company_id, operator_id, type, subject, status, vehicle_id,
				                     station_id, fueling_id, created_by, created_at, updated_at)
				values (:id, :reference, :scopeType, :company, :operator, :type, :subject, 'OPEN', :vehicle, :station, :fueling,
				        :actor, :now, :now)
				""")
			.param("id", id)
			.param("reference", reference)
			.param("scopeType", scope.type().name())
			.param("company", company ? scope.id() : null)
			.param("operator", company ? null : scope.id())
			.param("type", draft.type().name())
			.param("subject", draft.subject().strip())
			.param("vehicle", draft.vehicleId())
			.param("station", draft.stationId())
			.param("fueling", draft.fuelingId())
			.param("actor", actor.userId())
			.param("now", ts(now))
			.update();
		addMessage(actor, id, draft.body(), false, now);
		audit.record(new AuditLog.Actor(actor.userId(), actor.role()), "TICKET_OPENED", "TICKET", id, scope,
				Map.of("reference", reference, "type", draft.type().name()));
		events.publishEvent(new TicketOpened(id, draft.type().name(), company ? scope.id() : null, company ? null : scope.id()));
		return thread(id, false);
	}

	/** A reply. Only Operations may write an internal note. */
	@Transactional
	TicketThread reply(ZawedPrincipal actor, UUID ticketId, String body, boolean internal) {
		Ticket ticket = get(ticketId);
		boolean platform = actor.scope().isPlatform();
		if (internal && !platform) {
			throw new ApiException(HttpStatus.FORBIDDEN, "FORBIDDEN", "Only Operations writes internal notes");
		}
		if (ticket.status() == Status.CLOSED) {
			throw new ApiException(HttpStatus.CONFLICT, "TICKET_CLOSED", "This ticket is closed; open a new one");
		}
		Instant now = clock.instant();
		addMessage(actor, ticketId, body, internal, now);
		// Operations answering an open ticket takes it up. Only Operations changes a status: a company
		// or station writing on a resolved ticket shows as a new message, and Operations reopens it.
		if (platform && !internal && ticket.status() == Status.OPEN) {
			setStatus(ticketId, Status.IN_PROGRESS, ticket.assignedTo() != null ? ticket.assignedTo() : actor.userId(), now);
		}
		if (platform && !internal) {
			events.publishEvent(new TicketAnswered(ticketId, ticket.type().name(), ticket.companyId(), ticket.operatorId()));
		}
		return thread(ticketId, platform);
	}

	/** Operations moves a ticket along and, optionally, takes it. */
	@Transactional
	TicketThread update(ZawedPrincipal actor, UUID ticketId, Status status, boolean assignToMe) {
		Ticket ticket = get(ticketId);
		Instant now = clock.instant();
		setStatus(ticketId, status, assignToMe ? actor.userId() : ticket.assignedTo(), now);
		audit.record(new AuditLog.Actor(actor.userId(), actor.role()), "TICKET_STATUS_CHANGED", "TICKET", ticketId,
				ticket.companyId() != null ? Scope.company(ticket.companyId()) : Scope.operator(ticket.operatorId()),
				Map.of("from", ticket.status().name(), "to", status.name()));
		return thread(ticketId, true);
	}

	@Transactional(readOnly = true)
	Page<Ticket> list(UUID companyId, UUID operatorId, Status status, Type type, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Ticket> rows = jdbc.sql("""
				select * from tickets
				where (cast(:company as uuid) is null or company_id = :company)
				  and (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:status as text) is null or status = :status)
				  and (cast(:type as text) is null or type = :type)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("company", companyId)
			.param("operator", operatorId)
			.param("status", status == null ? null : status.name())
			.param("type", type == null ? null : type.name())
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Ticket::map)
			.list();
		return Page.of(rows, size, Ticket::id);
	}

	@Transactional(readOnly = true)
	TicketThread thread(UUID ticketId, boolean withInternal) {
		Ticket ticket = get(ticketId);
		List<Message> messages = jdbc.sql("""
				select * from ticket_messages where ticket_id = :ticket and (:internal or not internal) order by created_at, id
				""").param("ticket", ticketId).param("internal", withInternal).query(Message::map).list();
		return new TicketThread(ticket, messages);
	}

	private Ticket get(UUID id) {
		return jdbc.sql("select * from tickets where id = :id")
			.param("id", id)
			.query(Ticket::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Ticket"));
	}

	/** The table name is one of three constants above, never input. */
	private void requireVisible(String table, UUID id, String field) {
		if (id == null) {
			return;
		}
		boolean seen = jdbc.sql("select exists (select 1 from " + table + " where id = :id)")
			.param("id", id)
			.query(Boolean.class)
			.single();
		if (!seen) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "TICKET_LINK_NOT_FOUND", "The linked record was not found", field,
					null);
		}
	}

	private void addMessage(ZawedPrincipal actor, UUID ticketId, String body, boolean internal, Instant now) {
		jdbc.sql("""
				insert into ticket_messages (id, ticket_id, author_id, author_role, body, internal, created_at)
				values (:id, :ticket, :author, :role, :body, :internal, :now)
				""")
			.param("id", ids.next())
			.param("ticket", ticketId)
			.param("author", actor.userId())
			.param("role", actor.role().name())
			.param("body", body.strip())
			.param("internal", internal)
			.param("now", ts(now))
			.update();
	}

	private void setStatus(UUID ticketId, Status status, UUID assignee, Instant now) {
		boolean done = status == Status.RESOLVED || status == Status.CLOSED;
		jdbc.sql("""
				update tickets set status = :status, assigned_to = :assignee, updated_at = :now,
				       resolved_at = case when :done then coalesce(resolved_at, :now) else null end
				where id = :id
				""")
			.param("status", status.name())
			.param("assignee", assignee)
			.param("now", ts(now))
			.param("done", done)
			.param("id", ticketId)
			.update();
	}

}
