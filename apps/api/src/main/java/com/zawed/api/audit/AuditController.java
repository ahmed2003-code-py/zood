package com.zawed.api.audit;

import java.time.Instant;
import java.util.Map;
import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.transaction.annotation.Transactional;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import tools.jackson.core.type.TypeReference;
import tools.jackson.databind.ObjectMapper;

import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;

/** Read access to the audit trail. Row-level security limits a scoped reader to its own scope. */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/admin/audit")
@Tag(name = "audit")
class AuditController {

	record AuditEntry(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant occurredAt, UUID actorId, String actorRole,
			@Schema(requiredMode = RequiredMode.REQUIRED) String action,
			@Schema(requiredMode = RequiredMode.REQUIRED) String entityType, UUID entityId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String scopeType, UUID scopeId,
			@Schema(requiredMode = RequiredMode.REQUIRED) Map<String, Object> metadata, String requestId,
			String ipPrefix) {
	}

	private static final TypeReference<Map<String, Object>> MAP = new TypeReference<>() {
	};

	private final JdbcClient jdbc;
	private final ObjectMapper json;

	AuditController(JdbcClient jdbc, ObjectMapper json) {
		this.jdbc = jdbc;
		this.json = json;
	}

	@GetMapping
	@RequiresPermission(Permission.AUDIT_READ)
	@Transactional(readOnly = true)
	@Operation(operationId = "listAudit", summary = "Audit entries, newest first")
	Page<AuditEntry> list(@RequestParam(required = false) UUID actorId, @RequestParam(required = false) String entityType,
			@RequestParam(required = false) UUID entityId, @RequestParam(required = false) Instant from,
			@RequestParam(required = false) Instant to, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		int size = Page.limit(limit);
		var rows = jdbc.sql("""
				select id, occurred_at, actor_id, actor_role, action, entity_type, entity_id, scope_type, scope_id,
				       metadata::text as metadata, request_id, ip_prefix
				from audit_log
				where (cast(:actor as uuid) is null or actor_id = :actor)
				  and (cast(:entityType as text) is null or entity_type = :entityType)
				  and (cast(:entityId as uuid) is null or entity_id = :entityId)
				  and (cast(:from as timestamptz) is null or occurred_at >= :from)
				  and (cast(:to as timestamptz) is null or occurred_at < :to)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc
				limit :limit
				""")
			.param("actor", actorId)
			.param("entityType", entityType)
			.param("entityId", entityId)
			.param("from", from == null ? null : java.sql.Timestamp.from(from))
			.param("to", to == null ? null : java.sql.Timestamp.from(to))
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query((rs, n) -> new AuditEntry(rs.getObject("id", UUID.class), rs.getTimestamp("occurred_at").toInstant(),
					rs.getObject("actor_id", UUID.class), rs.getString("actor_role"), rs.getString("action"),
					rs.getString("entity_type"), rs.getObject("entity_id", UUID.class), rs.getString("scope_type"),
					rs.getObject("scope_id", UUID.class), json.readValue(rs.getString("metadata"), MAP),
					rs.getString("request_id"), rs.getString("ip_prefix")))
			.list();
		return Page.of(rows, size, AuditEntry::id);
	}

}
