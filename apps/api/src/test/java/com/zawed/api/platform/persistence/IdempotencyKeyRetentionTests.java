package com.zawed.api.platform.persistence;

import static org.assertj.core.api.Assertions.assertThat;

import java.sql.Connection;
import java.sql.SQLException;
import java.sql.Statement;
import java.time.Duration;
import java.util.UUID;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;

import com.zawed.api.support.IntegrationTest;
import com.zawed.api.support.Owner;

/**
 * Idempotency keys are purged once no client can still be retrying with them (V17, ADR 0022), in
 * bounded batches, by the one job allowed to; and the database refuses to let a key go any sooner or
 * at anyone else's hand, because a key that disappears early re-opens the double create it prevents.
 */
class IdempotencyKeyRetentionTests extends IntegrationTest {

	@Autowired
	IdempotencyKeyRetention retention;

	@Test
	void keysPastTheRetentionArePurgedAndRecentOnesKept() {
		UUID actor = UUID.randomUUID();
		for (int i = 0; i < 12; i++) {
			plant(actor, "old-key-" + i, "40 days");
		}
		plant(actor, "kept-key-29-days", "29 days"); // inside the 30-day default
		plant(actor, "kept-key-fresh", "1 minute");

		assertThat(retention.purge()).isGreaterThanOrEqualTo(12);
		assertThat(Owner.<String>queryList("select key from idempotency_keys where actor_id = ? order by key", actor))
			.containsExactly("kept-key-29-days", "kept-key-fresh");
	}

	@Test
	void theRetentionIsNeverShorterThanTheDatabaseFloor() {
		assertThat(IdempotencyKeyRetention.effective(Duration.ofDays(1))).isEqualTo(IdempotencyKeyRetention.FLOOR);
		assertThat(IdempotencyKeyRetention.effective(Duration.ofDays(30))).isEqualTo(Duration.ofDays(30));
		assertThat(IdempotencyKeyRetention.FLOOR).isEqualTo(Duration.ofDays(7));
	}

	@Test
	void onlyTheUserlessPlatformJobDeletesAndNeverAKeyYoungerThanTheFloor() throws SQLException {
		UUID actor = UUID.randomUUID();
		plant(actor, "young-key-2d", "2 days");
		plant(actor, "old-key-40d", "40 days");
		String delete = "delete from idempotency_keys where actor_id = '" + actor + "'";

		// The user themselves (their keys used to be theirs for every command): no longer deletable.
		assertThat(deleted(delete, "PLATFORM", actor)).as("the key's own user").isZero();
		// A signed-in Operations request runs in the platform scope with a user: not the job.
		assertThat(deleted(delete, "PLATFORM", UUID.randomUUID())).as("a platform user").isZero();
		assertThat(deleted(delete, "COMPANY", actor)).as("a company session").isZero();
		assertThat(deleted(delete, null, null)).as("no scope").isZero();
		// The job: the old key only. The two-day-old key stays whatever the caller's retention says.
		assertThat(deleted(delete, "PLATFORM", null)).as("the purge job").isEqualTo(1);
		assertThat(Owner.<String>queryList("select key from idempotency_keys where actor_id = ?", actor))
			.containsExactly("young-key-2d");
	}

	@Test
	void keysAreStillAppendOnlyForEveryoneElse() throws SQLException {
		UUID actor = UUID.randomUUID();
		plant(actor, "old-key-update", "40 days");
		try (Connection connection = app("PLATFORM", actor); Statement sql = connection.createStatement()) {
			org.assertj.core.api.Assertions
				.assertThatThrownBy(() -> sql.executeUpdate(
						"update idempotency_keys set resource_id = gen_random_uuid() where actor_id = '" + actor + "'"))
				.hasMessageContaining("permission denied");
		}
	}

	// ----------------------------------------------------------------------------------- helpers

	private static void plant(UUID actor, String key, String age) {
		Owner.update("""
				insert into idempotency_keys (actor_id, operation, key, request_hash, resource_id, created_at)
				values (?, 'wallet_adjustment.create', ?, repeat('a', 64), gen_random_uuid(), now() - cast(? as interval))
				""", actor, key, age);
	}

	/** Runs one statement as the runtime role in a scope and commits it; the row count. */
	private static int deleted(String statement, String scopeType, UUID userId) throws SQLException {
		try (Connection connection = app(scopeType, userId); Statement sql = connection.createStatement()) {
			int count = sql.executeUpdate(statement);
			connection.commit();
			return count;
		}
	}

	private static Connection app(String scopeType, UUID userId) throws SQLException {
		Connection connection = Owner.connect();
		connection.setAutoCommit(false);
		try (Statement sql = connection.createStatement()) {
			sql.execute("SET ROLE zawed_app");
			if (scopeType != null) {
				sql.execute("select set_config('app.scope_type', '" + scopeType + "', true)");
				if (!"PLATFORM".equals(scopeType)) {
					sql.execute("select set_config('app.scope_id', '" + UUID.randomUUID() + "', true)");
				}
			}
			if (userId != null) {
				sql.execute("select set_config('app.user_id', '" + userId + "', true)");
			}
		}
		return connection;
	}

}
