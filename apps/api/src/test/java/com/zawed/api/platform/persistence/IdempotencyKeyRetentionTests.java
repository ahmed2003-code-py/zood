package com.zawed.api.platform.persistence;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.sql.Connection;
import java.sql.ResultSet;
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
 * bounded batches, through one bounded function that refuses a cutoff inside seven days. The table
 * itself stays append only and per user for every session of the runtime role, a POS route (the
 * platform scope with no user) included: a key that disappears early re-opens the double create it
 * prevents.
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
	void theJobNeverAsksForLessThanTheDatabaseFloorPlusADay() {
		assertThat(IdempotencyKeyRetention.DATABASE_FLOOR).isEqualTo(Duration.ofDays(7));
		assertThat(IdempotencyKeyRetention.effective(Duration.ofDays(1))).isEqualTo(Duration.ofDays(8));
		assertThat(IdempotencyKeyRetention.effective(Duration.ofDays(7))).isEqualTo(Duration.ofDays(8));
		assertThat(IdempotencyKeyRetention.effective(Duration.ofDays(30))).isEqualTo(Duration.ofDays(30));
	}

	@Test
	void theFunctionRefusesACutoffInsideSevenDaysWhoeverCallsIt() throws SQLException {
		UUID actor = UUID.randomUUID();
		plant(actor, "young-key-2d", "2 days");
		plant(actor, "old-key-40d", "40 days");
		for (String cutoff : new String[] { "now() - interval '2 days'", "now() - interval '6 days 23 hours'", "null" }) {
			try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
				assertThatThrownBy(() -> sql.executeQuery("select purge_idempotency_keys(" + cutoff + ", 100)"))
					.as(cutoff).hasMessageContaining("7-day minimum retention");
			}
		}
		// Eight days is allowed: the old key goes, the two-day-old one stays.
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement();
				ResultSet rs = sql.executeQuery("select purge_idempotency_keys(now() - interval '8 days', 50000)")) {
			rs.next();
			assertThat(rs.getInt(1)).isGreaterThanOrEqualTo(1);
			connection.commit();
		}
		assertThat(Owner.<String>queryList("select key from idempotency_keys where actor_id = ?", actor))
			.containsExactly("young-key-2d");
	}

	@Test
	void aPosRouteSessionCanNeitherReadNorDeleteAnyonesKeys() throws SQLException {
		// Review finding (medium): every POS route runs in the platform scope with no user
		// (DeviceChannel, AgentSessions, FuelingEngine, OfflineSync, Receipts, Snapshots). Such a session
		// must see no key and delete none, whatever their age.
		UUID actor = UUID.randomUUID();
		plant(actor, "old-key-40d-pos", "40 days");
		plant(actor, "mid-key-10d-pos", "10 days");
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement();
				ResultSet rs = sql.executeQuery("select count(*) from idempotency_keys")) {
			rs.next();
			assertThat(rs.getLong(1)).as("keys visible to a POS-style session").isZero();
		}
		for (String scope : new String[] { "PLATFORM", "COMPANY", null }) {
			for (UUID user : new UUID[] { null, actor, UUID.randomUUID() }) {
				try (Connection connection = app(scope, user); Statement sql = connection.createStatement()) {
					assertThatThrownBy(() -> sql.executeUpdate("delete from idempotency_keys where actor_id = '" + actor + "'"))
						.as("delete as %s / %s", scope, user).hasMessageContaining("permission denied");
				}
			}
		}
		assertThat(Owner.<Long>queryOne("select count(*) from idempotency_keys where actor_id = ?", actor)).isEqualTo(2L);
	}

	@Test
	void keysAreStillNeverUpdated() throws SQLException {
		UUID actor = UUID.randomUUID();
		plant(actor, "old-key-update", "40 days");
		try (Connection connection = app("PLATFORM", actor); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate(
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

	/** A transaction as the runtime role, told what {@code ScopedTransactionManager} would tell it. */
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
