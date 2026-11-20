package com.zawed.api.notifications;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Timestamp;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.Collections;
import java.util.EnumMap;
import java.util.EnumSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Set;
import java.util.UUID;
import java.util.function.Supplier;
import java.util.regex.Pattern;
import java.util.stream.Collectors;

import tools.jackson.databind.ObjectMapper;

import io.micrometer.core.instrument.Counter;
import io.micrometer.core.instrument.MeterRegistry;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.jdbc.core.simple.JdbcClient.StatementSpec;
import org.springframework.stereotype.Service;
import org.springframework.transaction.PlatformTransactionManager;
import org.springframework.transaction.TransactionDefinition;
import org.springframework.transaction.annotation.Propagation;
import org.springframework.transaction.annotation.Transactional;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * The notifications store (V19): written inside the causing transaction, read by each user for
 * their own scope and role. Every read names the caller's audience explicitly and row-level
 * security names it again, so a query that forgot the filter would still show nothing foreign.
 */
@Service
class Notifications {

	private static final Logger log = LoggerFactory.getLogger(Notifications.class);

	/**
	 * What happened, and the parameter keys it may carry (contract §2). The web maps each type to its
	 * own sentence (both languages). The database holds the same key lists
	 * ({@code notification_param_keys}); a key may be left out when it does not apply, never added.
	 */
	enum Type {

		WALLET_LOW_BALANCE("balancePiastres", "thresholdPiastres", "transactionId"),
		TOPUP_CREDITED("amountPiastres", "transactionId"),
		TOPUP_REJECTED("amountPiastres"),
		TICKET_ANSWERED("ticketType"),
		TOPUP_AWAITING_APPROVAL("companyId", "amountPiastres"),
		WALLET_ADJUSTMENT_AWAITING_APPROVAL("companyId", "amountPiastres"),
		HARD_ALERT_RAISED("alertType", "severity", "companyId", "operatorId", "stationId", "fuelingId"),
		TICKET_OPENED("ticketType", "companyId", "operatorId"),
		PAYOUT_POSTED("stationId", "amountPiastres"),
		BANK_ACCOUNT_CHANGE_APPROVED("stationId"),
		BANK_ACCOUNT_CHANGE_REJECTED("stationId");

		private final Set<String> keys;

		Type(String... keys) {
			this.keys = Set.of(keys);
		}

		Set<String> keys() {
			return keys;
		}

	}

	/** What the notification points at, for the web's link. */
	enum EntityType {

		COMPANY_WALLET, TOPUP, WALLET_ADJUSTMENT, ALERT, TICKET, PAYOUT, STATION_BANK_ACCOUNT

	}

	/** Who should hear of it: everyone holding one of these roles in this scope. */
	record Audience(Scope scope, Set<Role> roles) {

		Audience {
			Objects.requireNonNull(scope, "scope");
			if (roles == null || roles.isEmpty()) {
				throw new IllegalArgumentException("An audience needs at least one role");
			}
			for (Role role : roles) {
				if (role.scopeType() != scope.type()) {
					throw new IllegalArgumentException(role + " does not exist in a " + scope.type() + " scope");
				}
			}
			roles = Collections.unmodifiableSet(EnumSet.copyOf(roles));
		}

		static Audience of(Scope scope, Role first, Role... more) {
			return new Audience(scope, EnumSet.of(first, more));
		}

		/** The roles as the {@code text[]} literal's elements, in a stable order. */
		String rolesCsv() {
			return roles.stream().map(Role::name).sorted().collect(Collectors.joining(","));
		}

	}

	/**
	 * A notification's parameters: ids, integers (piastres, millilitres) and codes (enum names) only,
	 * never text a person typed. The database checks the same rule ({@code notification_params_valid}).
	 */
	static final class Params {

		private static final Pattern KEY = Pattern.compile("[a-z][A-Za-z0-9]{0,39}");
		private static final Pattern CODE = Pattern.compile("[A-Z][A-Z0-9_]{0,63}");

		private final Map<String, Object> values = new LinkedHashMap<>();

		static Params of() {
			return new Params();
		}

		/** An id; a null id is left out (it does not apply). */
		Params id(String key, UUID id) {
			return id == null ? this : put(key, id.toString());
		}

		Params number(String key, long value) {
			return put(key, value);
		}

		/** An enum's name, or a name of the same shape another module sent (an alert or ticket type). */
		Params code(String key, String code) {
			if (code == null || !CODE.matcher(code).matches()) {
				throw new IllegalArgumentException("Not a code: " + key);
			}
			return put(key, code);
		}

		Map<String, Object> values() {
			return Collections.unmodifiableMap(values);
		}

		/** Refuses a key the type does not carry (B7 W5 review, L2), before the database would. */
		void checkFor(Type type) {
			for (String key : values.keySet()) {
				if (!type.keys().contains(key)) {
					throw new IllegalArgumentException(type + " carries no parameter " + key);
				}
			}
		}

		private Params put(String key, Object value) {
			if (!KEY.matcher(key).matches()) {
				throw new IllegalArgumentException("Not a parameter name: " + key);
			}
			values.put(key, value);
			return this;
		}

	}

	/** Everything a notification needs besides its type, built only when it is about to be written. */
	record Notice(Audience audience, EntityType entityType, UUID entityId, Params params) {
	}

	/** One notification as its reader sees it; {@code readAt} is null while unread. */
	record Notification(UUID id, Type type, EntityType entityType, UUID entityId, Map<String, Object> params,
			Instant createdAt, Instant readAt) {
	}

	/** The unread count, exact up to {@link #MAX_UNREAD_COUNT}; above it {@code capped} is set. */
	record UnreadCount(int count, boolean capped) {
	}

	/** The badge never needs more; counting further would read every unread row on each poll. */
	static final int MAX_UNREAD_COUNT = 99;

	/**
	 * How far behind the click "mark all read" sets the user's mark (V19,
	 * {@code notification_read_marks}). A notification is stamped at insert and visible at commit;
	 * the mark must stay behind every transaction still open when the user clicks, or it would cover
	 * a notification they never saw. Every transaction that writes one is a single fueling, sync
	 * item, top-up, adjustment, ticket or payout step, far shorter than this; it also absorbs clock
	 * differences between instances. What the user sees inside this window is marked row by row.
	 */
	static final Duration MARK_GRACE = Duration.ofMinutes(5);

	/** Counted per type when a notification is refused and its cause goes on without it. */
	static final String DROPPED_METRIC = "zawed.notifications.dropped";

	private final JdbcClient jdbc;
	private final ObjectMapper json;
	private final IdGenerator ids;
	private final Clock clock;
	private final TransactionTemplate savepoint;
	private final Map<Type, Counter> dropped = new EnumMap<>(Type.class);

	Notifications(JdbcClient jdbc, ObjectMapper json, IdGenerator ids, Clock clock,
			PlatformTransactionManager transactionManager, MeterRegistry meters) {
		this.jdbc = jdbc;
		this.json = json;
		this.ids = ids;
		this.clock = clock;
		// NESTED inside the cause's transaction is a JDBC savepoint: a failure rolls back to it and
		// leaves the cause's own work, and its transaction, intact (no rollback-only mark).
		this.savepoint = new TransactionTemplate(transactionManager);
		this.savepoint.setPropagationBehavior(TransactionDefinition.PROPAGATION_NESTED);
		for (Type type : Type.values()) {
			dropped.put(type, Counter.builder(DROPPED_METRIC)
				.description("Notifications refused and rolled back to their savepoint; the cause committed without them")
				.tag("type", type.name())
				.register(meters));
		}
	}

	// ------------------------------------------------------------------------------------ write

	/**
	 * Writes a notification in the caller's transaction, which must exist: a notification never
	 * commits without its cause, and an event published outside a transaction fails loudly.
	 *
	 * <p>The write runs in a savepoint (B7 W5 review, M1). If anything about it fails (building the
	 * notice, the parameter check, row-level security, a constraint, a trigger), it is rolled back to
	 * the savepoint, counted in {@value #DROPPED_METRIC} and logged as an error, and the cause goes on:
	 * a fueling, a sync item, a top-up decision or a ticket is a fact, and a notice about it is not a
	 * reason to refuse it. A dropped notice can be rebuilt from its cause; a refused fueling is fuel
	 * dispensed and not recorded.
	 *
	 * <p>The actor is the transaction's own user ({@code app_user_id()}), never a value passed in, so
	 * it cannot name someone else.
	 */
	@Transactional(propagation = Propagation.MANDATORY)
	void send(Type type, Supplier<Notice> notice) {
		try {
			savepoint.executeWithoutResult((status) -> write(type, notice.get()));
		}
		catch (RuntimeException ex) {
			dropped.get(type).increment();
			log.error("Notification {} was refused and dropped; its cause is recorded without it", type, ex);
		}
	}

	private void write(Type type, Notice notice) {
		notice.params().checkFor(type);
		jdbc.sql("""
				insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
				                           entity_id, params, actor_id, created_at)
				values (:id, :type, :scopeType, :scopeId, string_to_array(:roles, ','), :entityType, :entityId,
				        cast(:params as jsonb), app_user_id(), :now)
				""")
			.param("id", ids.next())
			.param("type", type.name())
			.param("scopeType", notice.audience().scope().type().name())
			.param("scopeId", notice.audience().scope().id())
			.param("roles", notice.audience().rolesCsv())
			.param("entityType", notice.entityType().name())
			.param("entityId", Objects.requireNonNull(notice.entityId(), "entityId"))
			.param("params", json.writeValueAsString(notice.params().values()))
			.param("now", ts(clock.instant()))
			.update();
	}

	// ------------------------------------------------------------------------------------- read

	/** Before any notification: the mark of a user who never marked all (they read one by one). */
	private static final Instant NO_MARK = Instant.EPOCH;

	/**
	 * The user's mark, read on its own (a primary-key lookup) and bound as {@code :mark}. Inline, as
	 * a subquery under {@code coalesce}, PostgreSQL cannot prove the comparison leakproof, so under
	 * row-level security it would stay a filter over the whole feed instead of an index condition.
	 */
	private Timestamp markOf(ZawedPrincipal me) {
		return ts(jdbc.sql("select read_up_to from notification_read_marks where user_id = :me")
			.param("me", me.userId())
			.query((rs, row) -> instant(rs, "read_up_to"))
			.optional()
			.orElse(NO_MARK));
	}

	/**
	 * The caller's notifications, newest first, paged by the keyset {@code (created_at, id)} (B7 W5
	 * review, L4): the cursor is the last row's pair, so rows with the same time are neither skipped
	 * nor repeated, and the row comparison is an index condition on {@code notifications_audience_idx}.
	 * With {@code unreadOnly}, only those after the user's mark and not read one by one.
	 */
	@Transactional(readOnly = true)
	Page<Notification> mine(ZawedPrincipal me, boolean unreadOnly, String cursor, Integer limit) {
		int size = Page.limit(limit);
		Keyset after = Keyset.parse(cursor);
		StatementSpec statement = audience(me, jdbc.sql(feedSql(me, unreadOnly, after != null))).param("limit", size + 1);
		if (after != null) {
			statement = statement.param("cursorAt", after.createdAt()).param("cursorId", after.id());
		}
		if (unreadOnly) {
			statement = statement.param("mark", markOf(me));
		}
		List<Notification> rows = statement.query(this::map).list();
		if (rows.size() <= size) {
			return new Page<>(List.copyOf(rows), null);
		}
		List<Notification> items = List.copyOf(rows.subList(0, size));
		return new Page<>(items, Keyset.of(items.getLast()));
	}

	/**
	 * Counts what is newer than the user's mark and not read one by one (B7 W5 review, M2): a range on
	 * the audience index from the mark onward, so the cost follows what arrived since the user last
	 * marked all read, not the size of the feed. Stops at {@link #MAX_UNREAD_COUNT} + 1.
	 */
	@Transactional(readOnly = true)
	UnreadCount unread(ZawedPrincipal me) {
		int counted = audience(me, jdbc.sql(unreadSql(me)))
			.param("mark", markOf(me))
			.param("cap", MAX_UNREAD_COUNT + 1)
			.query(Integer.class)
			.single();
		return new UnreadCount(Math.min(counted, MAX_UNREAD_COUNT), counted > MAX_UNREAD_COUNT);
	}

	/**
	 * Marks one of the caller's notifications read; marking it again changes nothing. A notification
	 * outside the caller's audience does not exist for them: 404.
	 */
	@Transactional
	void markRead(ZawedPrincipal me, UUID id) {
		boolean mine = audience(me,
				jdbc.sql("select exists (select 1 from notifications n where n.id = :id and %s)".formatted(audienceFilter(me))))
			.param("id", id)
			.query(Boolean.class)
			.single();
		if (!mine) {
			throw ApiException.notFound("Notification");
		}
		jdbc.sql("""
				insert into notification_reads (user_id, notification_id, read_at) values (:me, :id, :now)
				on conflict do nothing
				""").param("me", me.userId()).param("id", id).param("now", ts(clock.instant())).update();
	}

	/**
	 * Marks everything the caller can see now as read, in two bounded writes instead of one row per
	 * notification (B7 W5 review, M2): the user's mark moves to {@link #MARK_GRACE} before now, and
	 * what the caller sees inside that window gets read rows. A notification still uncommitted at
	 * this moment is not visible to either statement, and its time is inside the window (no writing
	 * transaction lasts that long), so it stays unread when it commits.
	 *
	 * <p>The mark is written first: its row lock queues two clicks at once (two tabs) one behind the
	 * other, so their read rows never cross into a deadlock, and the second one's statement sees the
	 * first one's rows. {@code greatest} keeps the later mark, and the database refuses any move back.
	 */
	@Transactional
	void markAllRead(ZawedPrincipal me) {
		Instant now = clock.instant();
		Instant upTo = now.minus(MARK_GRACE);
		jdbc.sql("""
				insert into notification_read_marks as m (user_id, read_up_to, marked_at) values (:me, :upTo, :now)
				on conflict (user_id) do update
				  set read_up_to = greatest(m.read_up_to, excluded.read_up_to), marked_at = excluded.marked_at
				""").param("me", me.userId()).param("upTo", ts(upTo)).param("now", ts(now)).update();
		audience(me, jdbc.sql("""
				insert into notification_reads (user_id, notification_id, read_at)
				select :me, n.id, :now from notifications n
				where %s
				  and n.created_at > :upTo
				  and not exists (select 1 from notification_reads r where r.user_id = :me and r.notification_id = n.id)
				order by n.id
				on conflict do nothing
				""".formatted(audienceFilter(me))))
			.param("now", ts(now))
			.param("upTo", ts(upTo))
			.update();
	}

	// ---------------------------------------------------------------------------------- helpers

	/**
	 * The feed query: binds {@code :me}, {@code :role}, the scope ({@link #audience}), {@code :limit},
	 * with a cursor {@code :cursorAt} and {@code :cursorId}, and for unread only {@code :mark}. Fixed strings chosen by flags:
	 * nothing from the request goes into the SQL text. Package-private so a test can read its plan.
	 */
	static String feedSql(ZawedPrincipal me, boolean unreadOnly, boolean withCursor) {
		return """
				select n.id, n.type, n.entity_type, n.entity_id, n.params, n.created_at,
				       coalesce(r.read_at, case when n.created_at <= m.read_up_to then m.marked_at end) as read_at
				from notifications n
				left join notification_reads r on r.notification_id = n.id and r.user_id = :me
				left join notification_read_marks m on m.user_id = :me
				where %s
				""".formatted(audienceFilter(me))
				+ (unreadOnly ? "  and n.created_at > :mark\n  and r.read_at is null\n" : "")
				+ (withCursor ? "  and (n.created_at, n.id) < (:cursorAt, :cursorId)\n" : "")
				+ "order by n.created_at desc, n.id desc\nlimit :limit";
	}

	/** The unread count: binds what {@link #audience} binds, {@code :mark} and {@code :cap}. */
	static String unreadSql(ZawedPrincipal me) {
		return """
				select count(*) from (
				  select 1 from notifications n
				  where %s
				    and n.created_at > :mark
				    and not exists (select 1 from notification_reads r where r.user_id = :me and r.notification_id = n.id)
				  limit :cap) unread
				""".formatted(audienceFilter(me));
	}

	/**
	 * The feed's cursor: the last row's {@code created_at} (epoch microseconds, the database's
	 * precision) and id, as {@code <micros>.<uuid>}. Opaque to the client; anything else is
	 * {@code 400 INVALID_CURSOR}, never a 500.
	 */
	record Keyset(Timestamp createdAt, UUID id) {

		private static final Pattern FORMAT = Pattern.compile("(-?[0-9]{1,19})\\.([0-9a-fA-F-]{36})");

		static String of(Notification last) {
			long micros = ChronoUnit.MICROS.between(Instant.EPOCH, last.createdAt());
			return micros + "." + last.id();
		}

		static Keyset parse(String value) {
			if (value == null || value.isBlank()) {
				return null;
			}
			var match = FORMAT.matcher(value);
			try {
				if (match.matches()) {
					Instant at = Instant.EPOCH.plus(Long.parseLong(match.group(1)), ChronoUnit.MICROS);
					return new Keyset(Timestamp.from(at), UUID.fromString(match.group(2)));
				}
			}
			catch (RuntimeException ex) {
				// falls through to the same answer as any other malformed cursor
			}
			throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_CURSOR", "Invalid cursor", "cursor", null);
		}

	}

	/**
	 * The caller's audience as indexable predicates on {@code notifications_audience_idx}. One of two
	 * fixed strings, chosen by the kind of scope; nothing from the request goes into the SQL text.
	 */
	private static String audienceFilter(ZawedPrincipal me) {
		String scope = me.scope().isPlatform() ? "n.audience_scope_type = 'PLATFORM' and n.audience_scope_id is null"
				: "n.audience_scope_type = :scopeType and n.audience_scope_id = :scopeId";
		return scope + " and :role = any(n.audience_roles)";
	}

	/** Binds what {@link #audienceFilter} and the per-user joins refer to. */
	static StatementSpec audience(ZawedPrincipal me, StatementSpec statement) {
		StatementSpec bound = statement.param("me", me.userId()).param("role", me.role().name());
		if (!me.scope().isPlatform()) {
			bound = bound.param("scopeType", me.scope().type().name()).param("scopeId", me.scope().id());
		}
		return bound;
	}

	@SuppressWarnings("unchecked")
	private Notification map(ResultSet rs, int row) throws SQLException {
		return new Notification(rs.getObject("id", UUID.class), Type.valueOf(rs.getString("type")),
				EntityType.valueOf(rs.getString("entity_type")), rs.getObject("entity_id", UUID.class),
				json.readValue(rs.getString("params"), Map.class), instant(rs, "created_at"), instant(rs, "read_at"));
	}

}
