package com.zawed.api.platform.crypto;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.stream.Collectors;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.dao.DataAccessException;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Component;
import org.springframework.transaction.TransactionStatus;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Scope;

/**
 * Moves every encrypted value to the current data key (ADR 0027), so a previous key can be retired.
 *
 * <p>For each {@link EncryptedColumn}, values whose key id is lower than the current one are read with
 * their own key and written again with the current key, bound to the same row. Nothing else in the row
 * changes: not {@code updated_at}, not who changed it last. The database allows exactly this change
 * and nothing more (V20: same length, from an older key into the deployment's current key, every
 * other column as it was).
 *
 * <p>The state is the data itself (the key id byte), so a run is idempotent and resumable: a value
 * already under the current key is never selected again. Work is done in small batches, each its own
 * short transaction: the rows are locked {@code FOR NO KEY UPDATE SKIP LOCKED}, so the job never waits
 * for an application transaction (a sign-in, a fueling on that tag) and cannot deadlock with the
 * fueling path's wallet-then-tag order. That lock is the one the update itself takes; it does not
 * conflict with the {@code FOR KEY SHARE} lock a new fueling's foreign key takes on its tag, so a
 * fueling never waits for the job either. A row another transaction holds is skipped and taken on the
 * next run. Two instances running it at once only share the work. A value that cannot be read (a key
 * this instance does not hold, or a value that does not verify) is left exactly as it is and counted.
 *
 * <p>The database accepts a re-encryption only into the deployment's current key id, which
 * {@link DataKeyState} records at every start (V20). An instance whose current key is not that one
 * (another instance started later with another configuration) does nothing and says so.
 *
 * <p>Progress is one log line per run with counts only, never a value: how many values were
 * re-encrypted, how many failed, and how many are still under other keys, per column and key id.
 *
 * <p>Scope escalation (reviewed, {@code ArchitectureTests}): it has no request and no user, and works
 * across every tenant, so it runs as the platform. It reads and writes only the encrypted columns.
 */
@Component
class DataKeyRotation {

	private static final Logger LOG = LoggerFactory.getLogger(DataKeyRotation.class);

	/** Values per transaction: a batch holds at most this many row locks, for a few milliseconds. */
	static final int BATCH = 100;

	/** A run stops a column after this many batches (10,000 values) and continues on the next run. */
	static final int MAX_BATCHES_PER_RUN = 100;

	private static final DbScope.Context PLATFORM = new DbScope.Context(Scope.PLATFORM, null);

	/** The lowest uuid: where every column's walk starts. */
	private static final UUID FIRST = new UUID(0, 0);

	private final JdbcClient jdbc;
	private final TransactionTemplate transactions;
	private final DataEncryptor encryptor;
	private final List<EncryptedColumn> columns;
	private final boolean enabled;
	private final AtomicBoolean settledReported = new AtomicBoolean();

	DataKeyRotation(JdbcClient jdbc, TransactionTemplate transactions, DataEncryptor encryptor,
			List<EncryptedColumn> columns, @Value("${zawed.data-key-rotation.enabled:true}") boolean enabled) {
		this.jdbc = jdbc;
		this.transactions = transactions;
		this.encryptor = encryptor;
		this.columns = columns.stream().sorted(Comparator.comparing(EncryptedColumn::name)).toList();
		this.enabled = enabled;
	}

	/** What one run did and found, per column. */
	record Run(int currentKeyId, List<ColumnRun> columns) {

		long reencrypted() {
			return columns.stream().mapToLong(ColumnRun::reencrypted).sum();
		}

		long failed() {
			return columns.stream().mapToLong(ColumnRun::failed).sum();
		}

		/** Values not under the current key after the run, in every column. */
		long remaining() {
			return columns.stream().mapToLong(ColumnRun::remaining).sum();
		}

		ColumnRun column(String name) {
			return columns.stream().filter((c) -> c.column().equals(name)).findFirst().orElseThrow();
		}

	}

	/**
	 * @param remainingByKeyId values still not under the current key, by the key id they carry (0 for a
	 *     value too short to carry one)
	 */
	record ColumnRun(String column, long reencrypted, long failed, Map<Integer, Long> remainingByKeyId) {

		long remaining() {
			return remainingByKeyId.values().stream().mapToLong(Long::longValue).sum();
		}

	}

	private record Row(UUID id, byte[] value) {
	}

	private record Batch(int rows, UUID last, long reencrypted, long failed) {
	}

	@Scheduled(initialDelayString = "${zawed.data-key-rotation.initial-delay:PT2M}",
			fixedDelayString = "${zawed.data-key-rotation.interval:PT10M}")
	void scheduled() {
		if (enabled) {
			run();
		}
	}

	Run run() {
		return run(BATCH, MAX_BATCHES_PER_RUN);
	}

	Run run(int batchSize, int maxBatches) {
		int current = encryptor.currentKeyId();
		int recorded = DbScope.runAs(PLATFORM, () -> transactions.execute((status) -> jdbc
			.sql("select coalesce(data_key_current_id(), 0)")
			.query(Integer.class)
			.single()));
		if (recorded != current) {
			// Every update would be refused (V20); this is a configuration overlap, not a failure of the values.
			LOG.warn("Data key rotation: this instance's current key {} is not the deployment's current key {} "
					+ "(another instance started with another configuration); nothing re-encrypted this run", current,
					recorded);
			return new Run(current, List.of());
		}
		List<ColumnRun> results = new ArrayList<>();
		for (EncryptedColumn column : columns) {
			try {
				results.add(DbScope.runAs(PLATFORM, () -> rotate(column, current, batchSize, maxBatches)));
			}
			catch (RuntimeException ex) {
				// One column's trouble does not stop the others; it is retried on the next run.
				LOG.error("Data key rotation: {} could not be processed this run", column.name(), ex);
			}
		}
		Run run = new Run(current, List.copyOf(results));
		report(run);
		return run;
	}

	private ColumnRun rotate(EncryptedColumn column, int current, int batchSize, int maxBatches) {
		Map<Integer, Long> remaining = remaining(column, current);
		if (remaining.keySet().stream().noneMatch((id) -> id > 0 && id < current)) {
			return new ColumnRun(column.name(), 0, 0, remaining);
		}
		long reencrypted = 0;
		long failed = 0;
		UUID after = FIRST;
		for (int i = 0; i < maxBatches; i++) {
			UUID from = after;
			Batch batch = transactions.execute((status) -> batch(status, column, current, from, batchSize));
			reencrypted += batch.reencrypted();
			failed += batch.failed();
			if (batch.rows() < batchSize) {
				break;
			}
			after = batch.last();
		}
		return new ColumnRun(column.name(), reencrypted, failed, remaining(column, current));
	}

	private Batch batch(TransactionStatus status, EncryptedColumn column, int current, UUID after, int limit) {
		// Walks the primary key from where the last batch stopped, so a value that fails is passed over
		// for the rest of this run instead of being picked up by every batch.
		List<Row> rows = jdbc.sql("""
				select id, %2$s from %1$s
				where id > :after and %2$s is not null and data_key_id(%2$s) < :current
				order by id limit :limit
				for no key update skip locked
				""".formatted(column.table(), column.column()))
			.param("after", after)
			.param("current", current)
			.param("limit", limit)
			.query((rs, n) -> new Row(rs.getObject(1, UUID.class), rs.getBytes(2)))
			.list();
		long reencrypted = 0;
		long failed = 0;
		for (Row row : rows) {
			byte[] next;
			try {
				next = encryptor.reencrypt(row.value(), column.binding().apply(row.id()));
			}
			catch (IllegalArgumentException ex) {
				failed++; // a key this instance does not hold, or a value that does not verify: left as it is
				continue;
			}
			// A savepoint per row: a refusal by the database (V20 guard) costs that row, not the batch.
			Object savepoint = status.createSavepoint();
			try {
				// Compare-and-set on the old value as well as the lock: a value is never re-encrypted twice.
				int updated = jdbc.sql("update %1$s set %2$s = :next where id = :id and %2$s = :previous"
					.formatted(column.table(), column.column()))
					.param("next", next)
					.param("id", row.id())
					.param("previous", row.value())
					.update();
				status.releaseSavepoint(savepoint);
				reencrypted += updated;
			}
			catch (DataAccessException ex) {
				status.rollbackToSavepoint(savepoint);
				failed++;
			}
		}
		return new Batch(rows.size(), rows.isEmpty() ? after : rows.getLast().id(), reencrypted, failed);
	}

	/** Values not under the current key, by key id. One scan of the column; these tables are small. */
	private Map<Integer, Long> remaining(EncryptedColumn column, int current) {
		Map<Integer, Long> counts = new TreeMap<>();
		transactions.execute((status) -> {
			jdbc.sql("""
					select coalesce(data_key_id(%2$s), 0) as key_id, count(*) as n from %1$s
					where %2$s is not null and data_key_id(%2$s) is distinct from :current
					group by 1
					""".formatted(column.table(), column.column()))
				.param("current", current)
				.query((rs) -> {
					counts.put(rs.getInt("key_id"), rs.getLong("n"));
				});
			return null;
		});
		return counts;
	}

	private void report(Run run) {
		if (run.remaining() == 0 && run.reencrypted() == 0 && run.failed() == 0) {
			// Said once per settled period, so a deploy's log shows it without repeating every run.
			if (settledReported.compareAndSet(false, true)) {
				LOG.info("Data key rotation: every encrypted value is under the current key {}", run.currentKeyId());
			}
			return;
		}
		settledReported.set(false);
		String detail = run.columns()
			.stream()
			.filter((c) -> c.remaining() > 0 || c.failed() > 0)
			.map((c) -> c.column() + " " + c.remainingByKeyId()
				.entrySet()
				.stream()
				.map((e) -> "key " + e.getKey() + "=" + e.getValue())
				.collect(Collectors.joining(",")) + (c.failed() > 0 ? " failed=" + c.failed() : ""))
			.collect(Collectors.joining("; "));
		boolean unreadable = run.columns()
			.stream()
			.flatMap((c) -> c.remainingByKeyId().keySet().stream())
			.anyMatch((id) -> !encryptor.knowsKey(id));
		if (unreadable || run.failed() > 0) {
			LOG.error("Data key rotation: current key {}; re-encrypted {}, failed {}; {} values not under the current "
					+ "key, some under a key this instance does not hold or not readable [{}]", run.currentKeyId(),
					run.reencrypted(), run.failed(), run.remaining(), detail);
		}
		else {
			LOG.info("Data key rotation: current key {}; re-encrypted {}, failed 0; {} values not under the current key "
					+ "[{}]", run.currentKeyId(), run.reencrypted(), run.remaining(), detail);
		}
	}

}
