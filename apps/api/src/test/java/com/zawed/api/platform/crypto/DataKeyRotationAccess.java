package com.zawed.api.platform.crypto;

import java.util.LinkedHashMap;
import java.util.Map;

import org.springframework.context.ApplicationContext;

/**
 * Lets tests in other packages drive the rotation job and build an encryptor for a given key ring,
 * without making either public in production code.
 */
public final class DataKeyRotationAccess {

	/** One run, in terms a test can assert on. */
	public record Result(long reencrypted, long failed, long remaining,
			Map<String, Map<Integer, Long>> remainingByColumn, Map<String, Long> failedByColumn) {
	}

	private DataKeyRotationAccess() {
	}

	public static Result run(ApplicationContext context) {
		return run(context, DataKeyRotation.BATCH, DataKeyRotation.MAX_BATCHES_PER_RUN);
	}

	public static Result run(ApplicationContext context, int batchSize, int maxBatches) {
		DataKeyRotation.Run run = context.getBean(DataKeyRotation.class).run(batchSize, maxBatches);
		Map<String, Map<Integer, Long>> remaining = new LinkedHashMap<>();
		Map<String, Long> failed = new LinkedHashMap<>();
		for (DataKeyRotation.ColumnRun column : run.columns()) {
			remaining.put(column.column(), column.remainingByKeyId());
			failed.put(column.column(), column.failed());
		}
		return new Result(run.reencrypted(), run.failed(), run.remaining(), remaining, failed);
	}

	/** An encryptor with exactly these keys, writing with {@code currentKeyId}. */
	public static DataEncryptor encryptor(int currentKeyId, Map<Integer, byte[]> keys) {
		return new DataEncryptor(currentKeyId, keys);
	}

}
