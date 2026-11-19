package com.zawed.api;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.ResultSet;
import java.sql.Statement;
import java.util.UUID;

import org.flywaydb.core.Flyway;
import org.junit.jupiter.api.Test;

import com.zawed.api.support.TestDatabase;

/**
 * The production-hardening migrations (V12 onwards) applied to data that already exists, the way a
 * deployment meets it: a fresh database is migrated to V11, legacy rows are planted, then the rest of
 * the migrations run. Proves the backfills (legacy single-step top-ups, Ops-recorded credits,
 * canonical plate keys) and that a plate collision stops the migration instead of merging vehicles.
 */
class MigrationCompatibilityTests {

	private static final UUID COMPANY = UUID.randomUUID();

	@Test
	void legacyRowsAreBackfilledAndStillReadable() throws Exception {
		String url = freshDatabase();
		flyway(url, "11").migrate();
		UUID confirmed = UUID.randomUUID();
		UUID direct = UUID.randomUUID();
		UUID pending = UUID.randomUUID();
		UUID vehicle = UUID.randomUUID();
		try (Connection c = DriverManager.getConnection(url, TestDatabase.USERNAME, TestDatabase.PASSWORD);
				Statement sql = c.createStatement()) {
			// Plant legacy rows as they were written before V12, without the application around them.
			sql.execute("set session_replication_role = replica");
			sql.execute(company());
			UUID txn = UUID.randomUUID();
			sql.execute("insert into ledger_transactions (id, source_type, source_id, company_id, posted_at) values ('" + txn
					+ "', 'TOPUP', '" + confirmed + "', '" + COMPANY + "', now())");
			sql.execute(topUp(confirmed, "CONFIRMED", "'" + UUID.randomUUID() + "'", "'" + txn + "'", "LEG1"));
			UUID txn2 = UUID.randomUUID();
			sql.execute("insert into ledger_transactions (id, source_type, source_id, company_id, posted_at) values ('" + txn2
					+ "', 'TOPUP', '" + direct + "', '" + COMPANY + "', now())");
			sql.execute(topUp(direct, "CONFIRMED", "null", "'" + txn2 + "'", "LEG2"));
			sql.execute(topUp(pending, "PENDING", "'" + UUID.randomUUID() + "'", "null", "LEG3"));
			sql.execute(vehicle(vehicle, "طبع1234"));
		}

		flyway(url, null).migrate();

		try (Connection c = DriverManager.getConnection(url, TestDatabase.USERNAME, TestDatabase.PASSWORD);
				Statement sql = c.createStatement()) {
			assertThat(one(sql, "select origin || '/' || legacy_single_step || '/' || verified_amount_piastres "
					+ "from topup_requests where id = '" + confirmed + "'")).isEqualTo("COMPANY_REQUEST/true/5000");
			assertThat(one(sql, "select origin || '/' || legacy_single_step from topup_requests where id = '" + direct + "'"))
				.isEqualTo("OPS_RECORDED/true");
			assertThat(one(sql, "select status || '/' || legacy_single_step || '/' || coalesce(verified_by::text, '-') "
					+ "from topup_requests where id = '" + pending + "'")).isEqualTo("PENDING/false/-");
			assertThat(one(sql, "select receiving_account from topup_requests where id = '" + pending + "'")).isEqualTo("MAIN");
			assertThat(one(sql, "select plate_key from vehicles where id = '" + vehicle + "'")).isEqualTo("1234طبع");
			assertThat(one(sql, "select count(*) from ledger_accounts where code = 'platform:adjustments'")).isEqualTo("1");
			// A legacy confirmed top-up is final, like any other.
			assertThatThrownBy(() -> sql.execute("update topup_requests set note = 'x' where id = '" + confirmed + "'"))
				.hasMessageContaining("already decided");
		}
	}

	@Test
	void aPlateCollisionStopsTheMigrationInsteadOfMergingTwoVehicles() throws Exception {
		String url = freshDatabase();
		flyway(url, "13").migrate();
		try (Connection c = DriverManager.getConnection(url, TestDatabase.USERNAME, TestDatabase.PASSWORD);
				Statement sql = c.createStatement()) {
			sql.execute("set session_replication_role = replica");
			sql.execute(company());
			// The same plate typed in two orders: two rows before V14, one plate after it.
			sql.execute(vehicle(UUID.randomUUID(), "طبع1234"));
			sql.execute(vehicle(UUID.randomUUID(), "1234طبع"));
		}
		assertThatThrownBy(() -> flyway(url, null).migrate()).hasStackTraceContaining("would share the plate key");
	}

	private static Flyway flyway(String url, String target) {
		var config = Flyway.configure()
			.dataSource(url, TestDatabase.USERNAME, TestDatabase.PASSWORD)
			.locations("classpath:db/migration");
		if (target != null) {
			config.target(target);
		}
		return config.load();
	}

	private static String freshDatabase() throws Exception {
		String base = TestDatabase.jdbcUrl();
		String name = "zawed_mig_" + UUID.randomUUID().toString().replace("-", "").substring(0, 12);
		try (Connection c = DriverManager.getConnection(base, TestDatabase.USERNAME, TestDatabase.PASSWORD);
				Statement sql = c.createStatement()) {
			sql.execute("create database " + name);
		}
		int slash = base.lastIndexOf('/');
		int query = base.indexOf('?', slash);
		return base.substring(0, slash + 1) + name + (query < 0 ? "" : base.substring(query));
	}

	private static String company() {
		return "insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes, "
				+ "low_balance_threshold_piastres, created_at, updated_at) values ('" + COMPANY
				+ "', 'Legacy Co', 'ش', 'L', 'ACTIVE', 180, 0, now(), now())";
	}

	private static String topUp(UUID id, String status, String requestedBy, String txn, String reference) {
		String decided = "PENDING".equals(status) ? "null" : "now()";
		return "insert into topup_requests (id, company_id, amount_piastres, bank_reference, bank_reference_key, transfer_date,"
				+ " status, requested_by, requested_at, decided_by, decided_at, ledger_transaction_id, created_at, updated_at)"
				+ " values ('" + id + "', '" + COMPANY + "', 5000, '" + reference + "', '" + reference + "', current_date, '"
				+ status + "', " + requestedBy + ", now(), " + ("PENDING".equals(status) ? "null" : "'" + UUID.randomUUID() + "'")
				+ ", " + decided + ", " + txn + ", now(), now())";
	}

	private static String vehicle(UUID id, String plateKey) {
		return "insert into vehicles (id, company_id, plate, plate_key, make, model, fuel_type, tank_capacity_ml, tier, status,"
				+ " created_at, updated_at) values ('" + id + "', '" + COMPANY + "', '" + plateKey + "', '" + plateKey
				+ "', 'M', 'M', 'DIESEL', 50000, 'NORMAL', 'ACTIVE', now(), now())";
	}

	private static String one(Statement sql, String query) throws Exception {
		try (ResultSet rs = sql.executeQuery(query)) {
			rs.next();
			return rs.getString(1);
		}
	}

}
