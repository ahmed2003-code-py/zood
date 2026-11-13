package com.zawed.api.audit;

import java.time.Clock;
import java.util.Map;
import java.util.UUID;

import jakarta.servlet.http.HttpServletRequest;

import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Propagation;
import org.springframework.transaction.annotation.Transactional;
import org.springframework.web.context.request.RequestAttributes;
import org.springframework.web.context.request.RequestContextHolder;
import org.springframework.web.context.request.ServletRequestAttributes;

import tools.jackson.databind.ObjectMapper;

import org.springframework.jdbc.core.simple.JdbcClient;

import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.web.ClientInfo;
import com.zawed.api.platform.web.RequestIdFilter;

/**
 * The append-only audit trail (PRD §6.4). Every privileged change records who did what to which
 * entity, in the same transaction as the change: if the change rolls back, so does its record, and
 * a change cannot commit without one. The runtime role may insert rows and never update or delete
 * them (V3).
 */
@Service
public class AuditLog {

	/** Who acted. {@code null} id for the system or an anonymous caller (a failed sign-in). */
	public record Actor(UUID id, Role role) {

		public static final Actor ANONYMOUS = new Actor(null, null);

	}

	private final JdbcClient jdbc;
	private final IdGenerator ids;
	private final Clock clock;
	private final ObjectMapper json;

	AuditLog(JdbcClient jdbc, IdGenerator ids, Clock clock, ObjectMapper json) {
		this.jdbc = jdbc;
		this.ids = ids;
		this.clock = clock;
		this.json = json;
	}

	/**
	 * Records an action. Must run inside the transaction that makes the change.
	 *
	 * @param scope the scope of the entity acted on, which decides who may later read the row
	 * @param metadata facts that explain the action (a reason, the new status); never secrets
	 */
	@Transactional(propagation = Propagation.MANDATORY)
	public void record(Actor actor, String action, String entityType, UUID entityId, Scope scope,
			Map<String, ?> metadata) {
		HttpServletRequest request = currentRequest();
		jdbc.sql("""
				insert into audit_log (id, occurred_at, actor_id, actor_role, action, entity_type, entity_id,
				                       scope_type, scope_id, metadata, request_id, ip_prefix)
				values (:id, :at, :actor, :role, :action, :entityType, :entityId, :scopeType, :scopeId,
				        cast(:metadata as jsonb), :requestId, :ip)
				""")
			.param("id", ids.next())
			.param("at", java.sql.Timestamp.from(clock.instant()))
			.param("actor", actor.id())
			.param("role", actor.role() == null ? null : actor.role().name())
			.param("action", action)
			.param("entityType", entityType)
			.param("entityId", entityId)
			.param("scopeType", scope.type().name())
			.param("scopeId", scope.id())
			.param("metadata", json.writeValueAsString(metadata == null ? Map.of() : metadata))
			.param("requestId", request == null ? null : RequestIdFilter.currentRequestId(request))
			.param("ip", request == null ? null : ClientInfo.addressPrefix(request))
			.update();
	}

	private static HttpServletRequest currentRequest() {
		RequestAttributes attributes = RequestContextHolder.getRequestAttributes();
		return attributes instanceof ServletRequestAttributes servlet ? servlet.getRequest() : null;
	}

}
