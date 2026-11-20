package com.zawed.api.fleet;

import static com.zawed.api.platform.persistence.Sql.ts;

import java.lang.management.ManagementFactory;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Component;
import org.springframework.transaction.support.TransactionTemplate;

import tools.jackson.core.type.TypeReference;
import tools.jackson.databind.ObjectMapper;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.fleet.FleetImports.Values;
import com.zawed.api.fleet.Vehicles.Draft;
import com.zawed.api.fleet.Vehicles.LimitKind;
import com.zawed.api.fleet.Vehicles.LimitPeriod;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;

/**
 * Runs confirmed imports (ADR 0026). PostgreSQL is the queue: a job is claimed with
 * {@code FOR UPDATE SKIP LOCKED} and a lease, so two instances never run one job and a crashed
 * instance's job is picked up again once its lease runs out.
 *
 * <p>Each row is its own short transaction: the vehicle, its limits and the row's outcome commit or
 * roll back together. Resuming therefore starts at the first row still {@code VALID}, and nothing is
 * created twice; the plate's unique index is the last defence if anything else registered the plate
 * meanwhile (the row then ends as {@code EXISTS}). A tick processes a bounded number of rows and
 * renews the lease as it goes.
 *
 * <p>Scope: the worker has no request, so it runs as the platform on behalf of the Ops user who
 * confirmed the job, recorded on the job ({@code DbScope.runAs}, a reviewed entry in
 * {@code ArchitectureTests}). Every insert still passes row-level security and the same-tenant
 * triggers, for the job's own company.
 */
@Component
class FleetImportWorker {

	private static final Logger LOG = LoggerFactory.getLogger(FleetImportWorker.class);

	static final Duration LEASE = Duration.ofMinutes(2);

	static final int ROWS_PER_TICK = 250;

	private final JdbcClient jdbc;
	private final TransactionTemplate tx;
	private final Vehicles vehicles;
	private final FleetImports imports;
	private final AuditLog audit;
	private final ObjectMapper json;
	private final Clock clock;
	private final boolean enabled;
	private final String owner = ManagementFactory.getRuntimeMXBean().getName() + ":" + UUID.randomUUID();

	FleetImportWorker(JdbcClient jdbc, TransactionTemplate tx, Vehicles vehicles, FleetImports imports, AuditLog audit,
			ObjectMapper json, Clock clock, @Value("${zawed.fleet-import.worker-enabled:true}") boolean enabled) {
		this.jdbc = jdbc;
		this.tx = tx;
		this.vehicles = vehicles;
		this.imports = imports;
		this.audit = audit;
		this.json = json;
		this.clock = clock;
		this.enabled = enabled;
	}

	@Scheduled(initialDelayString = "PT5S", fixedDelayString = "${zawed.fleet-import.poll-interval:PT2S}")
	void scheduled() {
		if (enabled) {
			runOnce(ROWS_PER_TICK, owner);
		}
	}

	/** Claims one job and processes up to {@code maxRows} of it. @return the job worked on, or null */
	UUID runOnce(int maxRows, String workerId) {
		Claim claim = platform(null, () -> tx.execute((status) -> claim(workerId)));
		if (claim == null) {
			return null;
		}
		ZawedPrincipal actor = new ZawedPrincipal(claim.confirmedBy(), null, Role.valueOf(claim.role()), Scope.PLATFORM,
				false);
		List<PendingRow> rows = platform(claim.confirmedBy(), () -> tx.execute((status) -> jdbc.sql("""
				select row_number, data from fleet_import_rows
				where job_id = :job and status = 'VALID' order by row_number limit :limit
				""").param("job", claim.jobId()).param("limit", maxRows).query((rs, n) -> new PendingRow(rs.getInt(1),
						json.readValue(rs.getString(2), new TypeReference<Map<String, Object>>() {
						}))).list()));
		for (PendingRow row : rows) {
			if (!process(claim, actor, row, workerId)) {
				return claim.jobId(); // another worker owns the job now; it finishes it
			}
		}
		if (rows.size() < maxRows) {
			platform(claim.confirmedBy(), () -> tx.execute((status) -> finish(claim, workerId)));
		}
		return claim.jobId();
	}

	private record Claim(UUID jobId, UUID companyId, UUID confirmedBy, String role) {
	}

	private record PendingRow(int number, Map<String, Object> data) {
	}

	private Claim claim(String workerId) {
		Instant now = clock.instant();
		return jdbc.sql("""
				update fleet_import_jobs set status = 'RUNNING', lease_owner = :owner, lease_until = :until,
				       started_at = coalesce(started_at, :now), updated_at = :now
				where id = (select id from fleet_import_jobs
				            where status = 'QUEUED' or (status = 'RUNNING' and (lease_until < :now or lease_owner = :owner))
				            order by confirmed_at, id limit 1 for update skip locked)
				returning id, company_id, confirmed_by, confirmed_by_role
				""")
			.param("owner", workerId)
			.param("until", ts(now.plus(LEASE)))
			.param("now", ts(now))
			.query((rs, n) -> new Claim(rs.getObject(1, UUID.class), rs.getObject(2, UUID.class),
					rs.getObject(3, UUID.class), rs.getString(4)))
			.optional()
			.orElse(null);
	}

	/** A row this worker no longer owns: another worker took the job after this one's lease ran out. */
	private static final class RowTakenOver extends RuntimeException {

		RowTakenOver(UUID jobId, int rowNumber) {
			super("Import row " + jobId + "/" + rowNumber + " is no longer pending", null, false, false);
		}

	}

	/**
	 * Processes one row; every outcome is recorded on the row, so a job never stalls on it.
	 * @return false when this worker has lost the job to another
	 */
	private boolean process(Claim claim, ZawedPrincipal actor, PendingRow row, String workerId) {
		try {
			attempt(claim, actor, row, workerId);
			return true;
		}
		catch (RowTakenOver ex) {
			return false;
		}
		catch (ApiException ex) {
			// The row's transaction rolled back whole; record why in a transaction of its own.
			boolean exists = "PLATE_IN_USE".equals(ex.code()) || "FLEET_CODE_IN_USE".equals(ex.code());
			return record(claim, row, exists ? FleetImports.RowStatus.EXISTS : FleetImports.RowStatus.FAILED,
					new FleetImports.RowError(ex.field() == null ? "row" : ex.field(), ex.code()),
					exists ? "existing_count" : "failed_count", workerId);
		}
		catch (RuntimeException ex) {
			// Anything else (a database refusal validation did not foresee, a bug) fails this row only.
			// Rethrowing would leave the row VALID, and the job would be reclaimed and fail on it forever.
			// Neither the row's data nor the exception's message is logged: a PostgreSQL constraint error
			// carries the whole failing row ("Failing row contains ..."), which is the company's data.
			LOG.error("Fleet import {} row {} failed unexpectedly: {} (SQLState {})", claim.jobId(), row.number(),
					ex.getClass().getSimpleName(), sqlState(ex));
			return record(claim, row, FleetImports.RowStatus.FAILED, new FleetImports.RowError("row", "INTERNAL"),
					"failed_count", workerId);
		}
	}

	private static String sqlState(Throwable ex) {
		for (Throwable t = ex; t != null; t = t.getCause()) {
			if (t instanceof java.sql.SQLException sql) {
				return sql.getSQLState();
			}
		}
		return "none";
	}

	private boolean record(Claim claim, PendingRow row, FleetImports.RowStatus status, FleetImports.RowError error,
			String counter, String workerId) {
		try {
			platform(claim.confirmedBy(), () -> tx.execute((s) -> {
				outcome(claim, row.number(), status, null, List.of(error), counter, workerId);
				return null;
			}));
			return true;
		}
		catch (RowTakenOver ex) {
			return false;
		}
	}

	/** The vehicle, its limits and the row's outcome, in one transaction: all of it commits, or none. */
	private void attempt(Claim claim, ZawedPrincipal actor, PendingRow row, String workerId) {
		platform(claim.confirmedBy(), () -> tx.execute((status) -> {
			Values v = imports.values(row.data());
			Vehicles.Vehicle vehicle = vehicles.create(actor, claim.companyId(), new Draft(v.departmentId(), v.plate(),
					v.make(), v.model(), v.year(), v.fuelType(), v.tankCapacityMl(), v.tier(), v.vipMonthlyCeilingMl(),
					v.fleetCode()));
			limit(actor, vehicle.id(), LimitPeriod.DAY, v.dailyLimitMl());
			limit(actor, vehicle.id(), LimitPeriod.WEEK, v.weeklyLimitMl());
			limit(actor, vehicle.id(), LimitPeriod.MONTH, v.monthlyLimitMl());
			outcome(claim, row.number(), FleetImports.RowStatus.IMPORTED, vehicle.id(), List.of(), "imported_count",
					workerId);
			return null;
		}));
	}

	private void limit(ZawedPrincipal actor, UUID vehicleId, LimitPeriod period, Long maxMl) {
		if (maxMl != null) {
			vehicles.setLimit(actor, vehicleId, LimitKind.VOLUME, period, maxMl);
		}
	}

	/** The row's outcome, its job counter and the lease, in the row's own transaction. */
	private void outcome(Claim claim, int rowNumber, FleetImports.RowStatus status, UUID vehicleId,
			List<FleetImports.RowError> errors, String counter, String workerId) {
		Instant now = clock.instant();
		int updated = jdbc.sql("""
				update fleet_import_rows set status = :status, vehicle_id = :vehicle, errors = cast(:errors as jsonb),
				       processed_at = :now
				where job_id = :job and row_number = :row and status = 'VALID'
				""")
			.param("status", status.name())
			.param("vehicle", vehicleId)
			.param("errors", json.writeValueAsString(errors))
			.param("now", ts(now))
			.param("job", claim.jobId())
			.param("row", rowNumber)
			.update();
		if (updated != 1) {
			// Another worker took this row after a lease expired: undo this attempt entirely.
			throw new RowTakenOver(claim.jobId(), rowNumber);
		}
		jdbc.sql("update fleet_import_jobs set " + counter + " = " + counter
				+ " + 1, lease_until = :until, updated_at = :now where id = :job and lease_owner = :owner")
			.param("until", ts(now.plus(LEASE)))
			.param("now", ts(now))
			.param("job", claim.jobId())
			.param("owner", workerId)
			.update();
	}

	private Void finish(Claim claim, String workerId) {
		Instant now = clock.instant();
		Map<String, Object> counts = jdbc.sql("""
				select count(*) filter (where status = 'VALID') as pending,
				       count(*) filter (where status = 'IMPORTED') as imported,
				       count(*) filter (where status = 'EXISTS') as existing,
				       count(*) filter (where status = 'FAILED') as failed,
				       count(*) as total
				from fleet_import_rows where job_id = :job
				""").param("job", claim.jobId()).query().singleRow();
		if (((Number) counts.get("pending")).longValue() > 0) {
			return null; // rows remain: the next tick continues
		}
		boolean clean = ((Number) counts.get("imported")).longValue() == ((Number) counts.get("total")).longValue();
		// The counters are recomputed from the rows: a row committed by a worker whose lease had just been
		// taken over updates no counter (its counter update matches no lease), so the running totals can
		// fall short. The rows are the record.
		int updated = jdbc.sql("""
				update fleet_import_jobs set status = :status, finished_at = :now, lease_owner = null, lease_until = null,
				       imported_count = :imported, existing_count = :existing, failed_count = :failed, updated_at = :now
				where id = :job and status = 'RUNNING' and lease_owner = :owner
				""")
			.param("imported", ((Number) counts.get("imported")).intValue())
			.param("existing", ((Number) counts.get("existing")).intValue())
			.param("failed", ((Number) counts.get("failed")).intValue())
			.param("status", clean ? "COMPLETED" : "COMPLETED_WITH_ERRORS")
			.param("now", ts(now))
			.param("job", claim.jobId())
			.param("owner", workerId)
			.update();
		if (updated == 1) {
			FleetImports.Job job = imports.get(claim.jobId());
			audit.record(new AuditLog.Actor(claim.confirmedBy(), Role.valueOf(claim.role())), "FLEET_IMPORT_COMPLETED",
					"FLEET_IMPORT", claim.jobId(), Scope.company(claim.companyId()),
					Map.of("imported", job.importedCount(), "existing", job.existingCount(), "failed", job.failedCount(),
							"status", job.status().name()));
			LOG.info("Fleet import {} finished: {} imported, {} existing, {} failed", claim.jobId(), job.importedCount(),
					job.existingCount(), job.failedCount());
		}
		return null;
	}

	private static <T> T platform(UUID userId, java.util.function.Supplier<T> work) {
		return DbScope.runAs(new DbScope.Context(Scope.PLATFORM, userId), work);
	}

}
