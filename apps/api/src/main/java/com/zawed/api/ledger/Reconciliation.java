package com.zawed.api.ledger;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.nullableLong;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;

import jakarta.servlet.http.HttpServletRequest;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Service;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.jobs.Job;
import com.zawed.api.platform.jobs.JobResult;
import com.zawed.api.platform.jobs.Jobs;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;
import com.zawed.api.platform.web.RequestIdFilter;
import org.springframework.transaction.annotation.Transactional;
import org.springframework.transaction.support.TransactionTemplate;
import org.springframework.web.context.request.RequestAttributes;
import org.springframework.web.context.request.RequestContextHolder;
import org.springframework.web.context.request.ServletRequestAttributes;

/**
 * The nightly check that every cached balance equals the sum of its ledger entries (PRD §4.2, plan
 * §3.5): company wallets since B3, station payables since B6 (V9). The comparison itself is
 * {@code ledger_reconcile}: it runs as the database owner because the job has no principal, takes
 * an advisory lock so one instance runs it at a time (ADR 0011), and reads one snapshot per kind.
 * A run that finds nothing is still recorded, so "last run" is always answerable.
 */
@Service
class Reconciliation {

	enum Trigger {

		SCHEDULED, MANUAL

	}

	enum FindingKind {

		/** A wallet's cached balance differs from the ledger; the ledger is the truth. */
		BALANCE_MISMATCH,
		/** A company exists without a wallet row. */
		MISSING_WALLET,
		/** A station payable's cached balance differs from the ledger; the ledger is the truth. */
		PAYABLE_MISMATCH,
		/** A station payable account exists without its cached row. */
		MISSING_PAYABLE

	}

	record Run(UUID id, Trigger trigger, UUID triggeredBy, Instant startedAt, Instant finishedAt, int walletsChecked,
			int payablesChecked, int findingsCount) {

		static Run map(ResultSet rs, int row) throws SQLException {
			return new Run(rs.getObject("id", UUID.class), Trigger.valueOf(rs.getString("trigger")),
					rs.getObject("triggered_by", UUID.class), instant(rs, "started_at"), instant(rs, "finished_at"),
					rs.getInt("wallets_checked"), rs.getInt("payables_checked"), rs.getInt("findings_count"));
		}

	}

	/**
	 * @param companyId the wallet's company, for a wallet finding; null otherwise
	 * @param stationId the payable's station, for a payable finding; null otherwise
	 */
	record Finding(UUID id, UUID runId, UUID companyId, UUID stationId, FindingKind kind, Long cachedBalancePiastres,
			Long ledgerBalancePiastres, Long differencePiastres, Instant createdAt) {

		static Finding map(ResultSet rs, int row) throws SQLException {
			return new Finding(rs.getObject("id", UUID.class), rs.getObject("run_id", UUID.class),
					rs.getObject("company_id", UUID.class), rs.getObject("station_id", UUID.class),
					FindingKind.valueOf(rs.getString("kind")),
					nullableLong(rs, "cached_balance_piastres"), nullableLong(rs, "ledger_balance_piastres"),
					nullableLong(rs, "difference_piastres"), instant(rs, "created_at"));
		}

	}

	private static final Logger log = LoggerFactory.getLogger(Reconciliation.class);

	private final JdbcClient jdbc;
	private final IdGenerator ids;
	private final Clock clock;
	private final TransactionTemplate tx;
	private final Jobs jobs;
	private final Job job;

	Reconciliation(JdbcClient jdbc, IdGenerator ids, Clock clock, TransactionTemplate tx, Jobs jobs) {
		this.jdbc = jdbc;
		this.ids = ids;
		this.clock = clock;
		this.tx = tx;
		this.jobs = jobs;
		// Nightly, so a missed night shows within two hours of the next one's time.
		this.job = jobs.define("ledger-reconciliation", Duration.ofHours(26));
	}

	/**
	 * Every night at 02:30 Cairo time, when fueling is at its quietest. Recorded in {@code job_runs}
	 * ({@code platform.jobs.Jobs}, ADR 0028): a run that another instance's lock (or tonight's earlier
	 * run) turned away is SKIPPED_LOCKED, an exception is FAILED, and the health check turns DOWN when no
	 * run has succeeded for {@code zawed.jobs.max-age.ledger-reconciliation} (26 hours by default).
	 */
	@Scheduled(cron = "0 30 2 * * *", zone = "Africa/Cairo")
	void nightly() {
		// Jobs.run catches, logs ("Job ledger-reconciliation failed", with the exception) and records a
		// failure, as this method's own catch did before.
		jobs.run(job, () -> {
			// A call on this, not on the proxy: the template opens the transaction (and so the
			// runtime-role check) that @Transactional would have opened for an outside caller.
			Optional<Run> run = tx.execute((status) -> run(Trigger.SCHEDULED, null));
			if (run.isEmpty()) {
				log.info("Reconciliation skipped: another instance is running it or already ran it tonight");
				return JobResult.skippedLocked();
			}
			if (run.get().findingsCount() > 0) {
				// A difference between a cached balance and the ledger is never routine: it must reach a
				// person. Until the alerts module (B4) raises it, this is the error the logs are watched for.
				log.error("Reconciliation {} found {} difference(s) across {} wallets and {} station payables; "
						+ "see the Ops reconciliation page", run.get().id(), run.get().findingsCount(),
						run.get().walletsChecked(), run.get().payablesChecked());
			}
			else {
				log.info("Reconciliation {}: {} wallets and {} station payables checked, no difference", run.get().id(),
						run.get().walletsChecked(), run.get().payablesChecked());
			}
			return JobResult.success(Map.of("wallets", (long) run.get().walletsChecked(), "payables",
					(long) run.get().payablesChecked(), "findings", (long) run.get().findingsCount()));
		});
	}

	/** Runs the comparison now. Empty when another run holds the lock. */
	@Transactional
	Optional<Run> run(Trigger trigger, ZawedPrincipal actor) {
		HttpServletRequest request = currentRequest();
		return jdbc.sql("select * from ledger_reconcile(:id, :trigger, :actor, :role, :now, :requestId)")
			.param("id", ids.next())
			.param("trigger", trigger.name())
			.param("actor", actor == null ? null : actor.userId())
			.param("role", actor == null ? null : actor.role().name())
			.param("now", ts(clock.instant()))
			.param("requestId", request == null ? null : RequestIdFilter.currentRequestId(request))
			.query(Run::map)
			.optional();
	}

	/** The Super Admin's "run now". */
	@Transactional
	Run runNow(ZawedPrincipal actor) {
		return run(Trigger.MANUAL, actor).orElseThrow(() -> new ApiException(HttpStatus.CONFLICT,
				"RECONCILIATION_RUNNING", "A reconciliation run is already in progress"));
	}

	@Transactional(readOnly = true)
	Page<Run> runs(String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Run> rows = jdbc.sql("""
				select * from reconciliation_runs
				where (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""").param("cursor", Page.cursor(cursor)).param("limit", size + 1).query(Run::map).list();
		return Page.of(rows, size, Run::id);
	}

	@Transactional(readOnly = true)
	Page<Finding> findings(UUID runId, String cursor, Integer limit) {
		jdbc.sql("select 1 from reconciliation_runs where id = :id")
			.param("id", runId)
			.query(Integer.class)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Reconciliation run"));
		int size = Page.limit(limit);
		List<Finding> rows = jdbc.sql("""
				select * from reconciliation_findings
				where run_id = :run and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("run", runId)
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Finding::map)
			.list();
		return Page.of(rows, size, Finding::id);
	}

	private static HttpServletRequest currentRequest() {
		RequestAttributes attributes = RequestContextHolder.getRequestAttributes();
		return attributes instanceof ServletRequestAttributes servlet ? servlet.getRequest() : null;
	}

}
