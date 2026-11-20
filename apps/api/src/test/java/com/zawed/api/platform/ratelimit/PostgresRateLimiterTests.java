package com.zawed.api.platform.ratelimit;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.sql.Connection;
import java.sql.SQLException;
import java.sql.Statement;
import java.time.Duration;
import java.util.List;
import java.util.UUID;

import javax.sql.DataSource;

import com.zaxxer.hikari.HikariDataSource;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.jdbc.core.simple.JdbcClient;

import com.zawed.api.platform.crypto.LookupHasher;
import com.zawed.api.platform.security.ScopedTransactionManager;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.IntegrationTest;
import com.zawed.api.support.Owner;
import com.zawed.api.support.TestDatabase;

/**
 * The shared limiter (V22, ADR 0012 amendment) against the real database. Two "instances" are two
 * limiters on two separate connection pools, as two application instances would be; a restart is a
 * new limiter on a new pool, with nothing carried over in memory.
 */
class PostgresRateLimiterTests extends IntegrationTest {

	@Autowired
	LookupHasher hasher;

	@Autowired
	DataSource dataSource;

	@Autowired
	RateLimitCounterRetention retention;

	@Test
	void twoInstancesCountingAtTheSameMomentShareOneBudget() throws Exception {
		String subject = subject();
		try (HikariDataSource poolA = pool(); HikariDataSource poolB = pool()) {
			PostgresRateLimiter a = limiter(poolA);
			PostgresRateLimiter b = limiter(poolB);
			List<Boolean> allowed = Concurrently.run(12, (i) -> {
				try {
					(i % 2 == 0 ? a : b).acquire("test-shared", subject, 5, Duration.ofHours(1));
					return true;
				}
				catch (RateLimitedException ex) {
					return false;
				}
			});
			assertThat(allowed.stream().filter(Boolean::booleanValue).count()).isEqualTo(5);
		}
		assertThat(Owner.<Integer>queryOne("select hits from rate_limit_counters where bucket = 'test-shared' and key_hash = ?",
				hasher.hash("rate-limit:test-shared", subject)))
			.isEqualTo(12);
	}

	@Test
	void aRestartedInstanceKeepsCountingWhereTheLastOneStopped() {
		String subject = subject();
		try (HikariDataSource before = pool()) {
			PostgresRateLimiter running = limiter(before);
			for (int i = 0; i < 3; i++) {
				running.acquire("test-restart", subject, 3, Duration.ofDays(1));
			}
		}
		try (HikariDataSource after = pool()) {
			PostgresRateLimiter restarted = limiter(after);
			assertThatThrownBy(() -> restarted.acquire("test-restart", subject, 3, Duration.ofDays(1)))
				.isInstanceOfSatisfying(RateLimitedException.class,
						(ex) -> assertThat(ex.retryAfterSeconds()).isBetween(1L, 86_400L));
		}
	}

	@Test
	void aNewWindowStartsAFreshBudgetAndKeysAndBucketsAreIndependent() {
		String subject = subject();
		try (HikariDataSource pool = pool()) {
			PostgresRateLimiter limiter = limiter(pool);
			limiter.acquire("test-window", subject, 1, Duration.ofHours(1));
			assertThatThrownBy(() -> limiter.acquire("test-window", subject, 1, Duration.ofHours(1)))
				.isInstanceOf(RateLimitedException.class);
			// Someone else, or the same subject in another bucket, is not affected.
			limiter.acquire("test-window", subject(), 1, Duration.ofHours(1));
			limiter.acquire("test-other", subject, 1, Duration.ofHours(1));

			Owner.update("""
					update rate_limit_counters set window_start = window_start - interval '1 hour',
					       expires_at = expires_at - interval '1 hour'
					where bucket = 'test-window' and key_hash = ?""", hasher.hash("rate-limit:test-window", subject));
			limiter.acquire("test-window", subject, 1, Duration.ofHours(1));
		}
	}

	@Test
	void onlyAKeyedHashOfTheSubjectIsStored() {
		String subject = "203.0.113.77";
		try (HikariDataSource pool = pool()) {
			limiter(pool).acquire("test-hash", subject, 10, Duration.ofMinutes(1));
		}
		List<String> keys = Owner.queryList("select key_hash from rate_limit_counters where bucket = 'test-hash'");
		assertThat(keys).isNotEmpty().allSatisfy((key) -> assertThat(key).matches("[0-9a-f]{64}").doesNotContain("203"));
		assertThat(keys).doesNotContain(com.zawed.api.platform.crypto.SecureTokens.sha256Hex(subject));
	}

	@Test
	void endedWindowsArePurgedAndRunningOnesKept() {
		String ended = hasher.hash("rate-limit:test-purge", subject());
		String running = hasher.hash("rate-limit:test-purge", subject());
		Owner.update("""
				insert into rate_limit_counters (bucket, key_hash, window_start, expires_at, hits)
				values ('test-purge', ?, now() - interval '2 days', now() - interval '1 day', 7),
				       ('test-purge', ?, now() - interval '1 minute', now() + interval '1 hour', 7)""", ended, running);
		assertThat(retention.purge()).isGreaterThanOrEqualTo(1);
		assertThat(Owner.<String>queryList("select key_hash from rate_limit_counters where bucket = 'test-purge'"))
			.containsExactly(running);
	}

	@Test
	void theRuntimeRoleCanCountButNeverReadResetOrDeleteACounter() throws SQLException {
		for (String sql : new String[] { "select * from rate_limit_counters", "delete from rate_limit_counters",
				"update rate_limit_counters set hits = 0",
				"insert into rate_limit_counters values ('x-y', '" + "0".repeat(64) + "', now(), now() + interval '1 hour', 1)" }) {
			try (Connection connection = dataSource.getConnection(); Statement statement = connection.createStatement()) {
				assertThatThrownBy(() -> statement.execute(sql)).as(sql).hasMessageContaining("permission denied");
			}
		}
		try (Connection connection = dataSource.getConnection(); Statement statement = connection.createStatement()) {
			assertThatThrownBy(() -> statement.executeQuery("select * from rate_limit_hit('test-guard', '" + "0".repeat(64)
					+ "', 0)")).hasMessageContaining("a window is 1 second to 7 days");
		}
	}

	// ------------------------------------------------------------------------------------ helpers

	/** A second application's connection pool: the owner's login, switched to the runtime role. */
	private static HikariDataSource pool() {
		HikariDataSource pool = new HikariDataSource();
		pool.setJdbcUrl(TestDatabase.jdbcUrl());
		pool.setUsername(TestDatabase.USERNAME);
		pool.setPassword(TestDatabase.PASSWORD);
		pool.setConnectionInitSql("SET ROLE zawed_app");
		pool.setMaximumPoolSize(6);
		return pool;
	}

	private PostgresRateLimiter limiter(DataSource pool) {
		return new PostgresRateLimiter(JdbcClient.create(pool), new ScopedTransactionManager(pool), hasher);
	}

	private static String subject() {
		return "subject-" + UUID.randomUUID();
	}

}
