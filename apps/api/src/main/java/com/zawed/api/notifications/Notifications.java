package com.zawed.api.notifications;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.Collections;
import java.util.EnumSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Set;
import java.util.UUID;
import java.util.regex.Pattern;
import java.util.stream.Collectors;

import tools.jackson.databind.ObjectMapper;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.jdbc.core.simple.JdbcClient.StatementSpec;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Propagation;
import org.springframework.transaction.annotation.Transactional;

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

	/** What happened. The web maps each to its own sentence (both languages). */
	enum Type {

		WALLET_LOW_BALANCE, TOPUP_CREDITED, TOPUP_REJECTED, TICKET_ANSWERED, TOPUP_AWAITING_APPROVAL,
		WALLET_ADJUSTMENT_AWAITING_APPROVAL, HARD_ALERT_RAISED, TICKET_OPENED, PAYOUT_POSTED,
		BANK_ACCOUNT_CHANGE_APPROVED, BANK_ACCOUNT_CHANGE_REJECTED

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
		private static final int MAX_KEYS = 8;

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

		private Params put(String key, Object value) {
			if (!KEY.matcher(key).matches()) {
				throw new IllegalArgumentException("Not a parameter name: " + key);
			}
			values.put(key, value);
			if (values.size() > MAX_KEYS) {
				throw new IllegalArgumentException("A notification carries at most " + MAX_KEYS + " parameters");
			}
			return this;
		}

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

	private final JdbcClient jdbc;
	private final ObjectMapper json;
	private final IdGenerator ids;
	private final Clock clock;

	Notifications(JdbcClient jdbc, ObjectMapper json, IdGenerator ids, Clock clock) {
		this.jdbc = jdbc;
		this.json = json;
		this.ids = ids;
		this.clock = clock;
	}

	// ------------------------------------------------------------------------------------ write

	/**
	 * Writes a notification in the caller's transaction, which must exist: a notification is part of
	 * its cause and commits or rolls back with it. The actor is the transaction's own user
	 * ({@code app_user_id()}), never a value passed in, so it cannot name someone else.
	 */
	@Transactional(propagation = Propagation.MANDATORY)
	void send(Type type, Audience audience, EntityType entityType, UUID entityId, Params params) {
		jdbc.sql("""
				insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
				                           entity_id, params, actor_id, created_at)
				values (:id, :type, :scopeType, :scopeId, string_to_array(:roles, ','), :entityType, :entityId,
				        cast(:params as jsonb), app_user_id(), :now)
				""")
			.param("id", ids.next())
			.param("type", type.name())
			.param("scopeType", audience.scope().type().name())
			.param("scopeId", audience.scope().id())
			.param("roles", audience.rolesCsv())
			.param("entityType", entityType.name())
			.param("entityId", Objects.requireNonNull(entityId, "entityId"))
			.param("params", json.writeValueAsString(params.values()))
			.param("now", ts(clock.instant()))
			.update();
	}

	// ------------------------------------------------------------------------------------- read

	/** The caller's notifications, newest first; with {@code unreadOnly}, only those not yet read. */
	@Transactional(readOnly = true)
	Page<Notification> mine(ZawedPrincipal me, boolean unreadOnly, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Notification> rows = audience(me, jdbc.sql("""
				select n.id, n.type, n.entity_type, n.entity_id, n.params, n.created_at, r.read_at
				from notifications n
				left join notification_reads r on r.notification_id = n.id and r.user_id = :me
				where %s
				  and (not :unread or r.read_at is null)
				  and (cast(:cursor as uuid) is null or n.id < :cursor)
				order by n.id desc
				limit :limit
				""".formatted(audienceFilter(me))))
			.param("unread", unreadOnly)
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(this::map)
			.list();
		return Page.of(rows, size, Notification::id);
	}

	@Transactional(readOnly = true)
	UnreadCount unread(ZawedPrincipal me) {
		int counted = audience(me, jdbc.sql("""
				select count(*) from (
				  select 1 from notifications n
				  where %s
				    and not exists (select 1 from notification_reads r where r.user_id = :me and r.notification_id = n.id)
				  limit :cap) unread
				""".formatted(audienceFilter(me))))
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
	 * Marks every notification the caller can see now as read. Bounded by retention: at most what the
	 * last 90 days addressed to this user's scope and role.
	 */
	@Transactional
	int markAllRead(ZawedPrincipal me) {
		return audience(me, jdbc.sql("""
				insert into notification_reads (user_id, notification_id, read_at)
				select :me, n.id, :now from notifications n
				where %s
				  and not exists (select 1 from notification_reads r where r.user_id = :me and r.notification_id = n.id)
				on conflict do nothing
				""".formatted(audienceFilter(me))))
			.param("now", ts(clock.instant()))
			.update();
	}

	// ---------------------------------------------------------------------------------- helpers

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
	private static StatementSpec audience(ZawedPrincipal me, StatementSpec statement) {
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
