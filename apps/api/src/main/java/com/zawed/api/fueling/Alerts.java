package com.zawed.api.fueling;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import tools.jackson.databind.ObjectMapper;

import org.springframework.context.event.EventListener;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.ledger.WalletLowBalance;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * Hard alerts (PRD §10): raised in the transaction of the event that causes them, never blocking it,
 * always visible to Operations. A company sees the ones meant for it; the VIP ceiling alert is for
 * Operations only (PRD §4.5, the ceiling is invisible to the company).
 */
@Service
class Alerts {

	enum Type {

		TANK_CAPACITY_EXCEEDED, VIP_CEILING_PASSED, LOW_BALANCE

	}

	enum Severity {

		WARNING, CRITICAL

	}

	enum Status {

		OPEN, ACKNOWLEDGED

	}

	record Alert(UUID id, Type type, Severity severity, Status status, UUID companyId, UUID operatorId,
			UUID stationId, UUID vehicleId, UUID fuelingId, Map<String, Object> details, boolean companyVisible,
			Instant createdAt, UUID acknowledgedBy, Instant acknowledgedAt) {
	}

	/** Where an alert points. Null fields do not apply. */
	record Subject(UUID companyId, UUID operatorId, UUID stationId, UUID vehicleId, UUID fuelingId) {
	}

	private final JdbcClient jdbc;
	private final ObjectMapper json;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;

	Alerts(JdbcClient jdbc, ObjectMapper json, AuditLog audit, IdGenerator ids, Clock clock) {
		this.jdbc = jdbc;
		this.json = json;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
	}

	/** Raises an alert in the caller's transaction, which must run in the platform scope. */
	void raise(Type type, Severity severity, Subject subject, Map<String, Object> details, boolean companyVisible) {
		jdbc.sql("""
				insert into alerts (id, type, severity, status, company_id, operator_id, station_id, vehicle_id, fueling_id,
				                    details, company_visible, created_at)
				values (:id, :type, :severity, 'OPEN', :company, :operator, :station, :vehicle, :fueling,
				        cast(:details as jsonb), :visible, :now)
				""")
			.param("id", ids.next())
			.param("type", type.name())
			.param("severity", severity.name())
			.param("company", subject.companyId())
			.param("operator", subject.operatorId())
			.param("station", subject.stationId())
			.param("vehicle", subject.vehicleId())
			.param("fueling", subject.fuelingId())
			.param("details", json.writeValueAsString(details))
			.param("visible", companyVisible)
			.param("now", ts(clock.instant()))
			.update();
	}

	/**
	 * PRD §4.7: a posting took the wallet below its threshold. Runs inside the posting transaction
	 * (a fueling under the engine's platform scope, or a Super Admin adjustment), so the alert
	 * commits or rolls back with the money.
	 */
	@EventListener
	void onLowBalance(WalletLowBalance event) {
		raise(Type.LOW_BALANCE, Severity.WARNING, new Subject(event.companyId(), null, null, null, null),
				Map.of("balancePiastres", event.balancePiastres(), "thresholdPiastres", event.thresholdPiastres(),
						"transactionId", event.transactionId().toString()),
				true);
	}

	// ------------------------------------------------------------------------------------- reads

	@Transactional(readOnly = true)
	Page<Alert> list(UUID companyId, Status status, Type type, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Alert> rows = jdbc.sql("""
				select * from alerts
				where (cast(:company as uuid) is null or company_id = :company)
				  and (cast(:status as text) is null or status = :status)
				  and (cast(:type as text) is null or type = :type)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("company", companyId)
			.param("status", status == null ? null : status.name())
			.param("type", type == null ? null : type.name())
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(this::map)
			.list();
		return Page.of(rows, size, Alert::id);
	}

	/** Operations has seen the alert. Acknowledging twice is a no-op. */
	@Transactional
	Alert acknowledge(ZawedPrincipal actor, UUID id) {
		Alert alert = jdbc.sql("select * from alerts where id = :id for update")
			.param("id", id)
			.query(this::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Alert"));
		if (alert.status() == Status.ACKNOWLEDGED) {
			return alert;
		}
		if (!actor.scope().isPlatform()) {
			throw new ApiException(HttpStatus.FORBIDDEN, "FORBIDDEN", "You do not have permission to do this");
		}
		jdbc.sql("update alerts set status = 'ACKNOWLEDGED', acknowledged_by = :actor, acknowledged_at = :now where id = :id")
			.param("id", id)
			.param("actor", actor.userId())
			.param("now", ts(clock.instant()))
			.update();
		audit.record(new AuditLog.Actor(actor.userId(), actor.role()), "ALERT_ACKNOWLEDGED", "ALERT", id,
				alert.companyId() == null ? Scope.PLATFORM : Scope.company(alert.companyId()),
				Map.of("type", alert.type().name()));
		return jdbc.sql("select * from alerts where id = :id").param("id", id).query(this::map).single();
	}

	@SuppressWarnings("unchecked")
	private Alert map(ResultSet rs, int row) throws SQLException {
		return new Alert(rs.getObject("id", UUID.class), Type.valueOf(rs.getString("type")),
				Severity.valueOf(rs.getString("severity")), Status.valueOf(rs.getString("status")),
				rs.getObject("company_id", UUID.class), rs.getObject("operator_id", UUID.class),
				rs.getObject("station_id", UUID.class), rs.getObject("vehicle_id", UUID.class),
				rs.getObject("fueling_id", UUID.class), json.readValue(rs.getString("details"), Map.class),
				rs.getBoolean("company_visible"), instant(rs, "created_at"), rs.getObject("acknowledged_by", UUID.class),
				instant(rs, "acknowledged_at"));
	}

}
