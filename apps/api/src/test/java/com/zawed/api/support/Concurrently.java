package com.zawed.api.support;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

/**
 * Runs {@code n} calls at the same moment, each on its own thread, and returns their results in
 * submission order. A call that throws fails the test through {@code Future.get}. For races on real
 * database rows: locks, unique keys and idempotency are tested against PostgreSQL, not mocks.
 */
public final class Concurrently {

	@FunctionalInterface
	public interface Call<T> {

		T run(int index) throws Exception;

	}

	private Concurrently() {
	}

	public static <T> List<T> run(int n, Call<T> call) throws Exception {
		CountDownLatch start = new CountDownLatch(1);
		try (ExecutorService pool = Executors.newFixedThreadPool(n)) {
			List<Future<T>> futures = new ArrayList<>();
			for (int i = 0; i < n; i++) {
				int index = i;
				futures.add(pool.submit(() -> {
					start.await();
					return call.run(index);
				}));
			}
			start.countDown();
			List<T> results = new ArrayList<>();
			for (Future<T> future : futures) {
				results.add(future.get(60, TimeUnit.SECONDS));
			}
			return results;
		}
	}

}
