package com.zawed.api.platform.jobs;

import java.lang.management.ManagementFactory;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.concurrent.ConcurrentSkipListMap;
import java.util.function.Supplier;
import java.util.regex.Pattern;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.core.env.Environment;
import org.springframework.stereotype.Component;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.jobs.JobResult.Outcome;

/**
 * Runs a scheduled job and leaves a record of it (V21, ADR 0028). Every {@code @Scheduled} job that
 * works on the database calls {@link #run}: the job's own body still does exactly what it did,
 * under the lock or lease it already had (an advisory lock in {@code ledger_reconcile}; none for the
 * purges, where two instances only share the work), and says what came of it. This class adds no
 * lock. It records the outcome in {@code job_runs}, in a transaction of its own after the body, so a
 * job that silently stopped shows in the health check ({@link JobsHealth}).
 *
 * <p>A failure is caught, logged and recorded with its class name only, never its message (a
 * PostgreSQL constraint error quotes the failing row). If the record itself cannot be written (the
 * database is down), the run is logged and the job carries on: the record never changes what the job
 * does.
 */
@Component
public class Jobs {

	private static final Logger LOG = LoggerFactory.getLogger(Jobs.class);

	/** The same rule as V21's {@code job_runs.job_name} check. */
	static final Pattern NAME = Pattern.compile("[a-z][a-z0-9-]{0,62}");

	private final JobRunStore store;
	private final TransactionTemplate tx;
	private final IdGenerator ids;
	private final Clock clock;
	private final Environment environment;
	private final Map<String, Job> defined = new ConcurrentSkipListMap<>();
	private final String instance = instanceId(ManagementFactory.getRuntimeMXBean().getName());
	private final Instant startedAt;

	Jobs(JobRunStore store, TransactionTemplate tx, IdGenerator ids, Clock clock, Environment environment) {
		this.store = store;
		this.tx = tx;
		this.ids = ids;
		this.clock = clock;
		this.environment = environment;
		this.startedAt = clock.instant();
	}

	/**
	 * Declares a job, so the health check expects it even before its first run. Called once, by the
	 * job's own constructor.
	 * @param defaultMaxAge how long it may go without a success; {@code zawed.jobs.max-age.<name>} overrides it
	 */
	public Job define(String name, Duration defaultMaxAge) {
		if (!NAME.matcher(name).matches()) {
			throw new IllegalArgumentException("Job name " + name + " is not lower-case words joined by dashes");
		}
		Duration maxAge = environment.getProperty("zawed.jobs.max-age." + name, Duration.class, defaultMaxAge);
		if (maxAge.isNegative() || maxAge.isZero()) {
			throw new IllegalArgumentException("zawed.jobs.max-age." + name + " must be positive");
		}
		Job job = new Job(name, maxAge);
		if (defined.putIfAbsent(name, job) != null) {
			throw new IllegalStateException("Job " + name + " is defined twice");
		}
		return job;
	}

	/** Runs the body, records the run, and returns what came of it. Never throws for a failed body. */
	public JobResult run(Job job, Supplier<JobResult> body) {
		Instant started = clock.instant();
		JobResult result;
		String errorClass = null;
		try {
			result = Objects.requireNonNull(body.get(), "a job body returns its result");
		}
		catch (RuntimeException ex) {
			LOG.error("Job {} failed", job.name(), ex);
			result = JobResult.failed();
			errorClass = errorClass(ex);
		}
		record(job, started, result, errorClass);
		return result;
	}

	private void record(Job job, Instant started, JobResult result, String errorClass) {
		Instant finished = clock.instant();
		try {
			tx.executeWithoutResult((status) -> store.record(ids.next(), job.name(), instance, started,
					finished.isBefore(started) ? started : finished, result.outcome(), result.counts(), errorClass));
		}
		catch (RuntimeException ex) {
			LOG.warn("Job {} ({}) could not be recorded: {}", job.name(), result.outcome(), ex.getClass().getName());
		}
		if (result.outcome() == Outcome.SKIPPED_LOCKED) {
			LOG.debug("Job {} skipped: another instance holds its lock", job.name());
		}
	}

	/** Every job declared in this application, by name. */
	List<Job> defined() {
		return List.copyOf(defined.values());
	}

	/** When this instance started: the reference for a job that has never succeeded. */
	Instant startedAt() {
		return startedAt;
	}

	/** The exception's class name, reduced to what {@code job_runs.error_class} accepts. Never its message. */
	static String errorClass(Throwable ex) {
		String name = ex.getClass().getName().replaceAll("[^A-Za-z0-9_$.]", "_");
		if (!Character.isLetter(name.charAt(0)) && name.charAt(0) != '_' && name.charAt(0) != '$') {
			name = "_" + name;
		}
		return name.length() > 200 ? name.substring(0, 200) : name;
	}

	/** "pid@host", reduced to what {@code job_runs.instance_id} accepts. */
	static String instanceId(String raw) {
		String cleaned = raw == null ? "" : raw.replaceAll("[^A-Za-z0-9@._:-]", "-");
		if (cleaned.isEmpty()) {
			return "unknown";
		}
		return cleaned.length() > 128 ? cleaned.substring(0, 128) : cleaned;
	}

}
