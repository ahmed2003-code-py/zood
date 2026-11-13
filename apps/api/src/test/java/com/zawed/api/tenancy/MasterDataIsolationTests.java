package com.zawed.api.tenancy;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.sql.Connection;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.util.List;
import java.util.UUID;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import com.zawed.api.support.IntegrationTest;
import com.zawed.api.support.Owner;

/**
 * Row-level security on the B2 tables, straight against the database as the runtime role: what a
 * scope can see and write whatever the application code does.
 */
class MasterDataIsolationTests extends IntegrationTest {

	private static final List<String> COMPANY_TABLES = List.of("companies", "departments", "vehicles", "rfid_tags",
			"limit_rules");

	private static final List<String> OPERATOR_TABLES = List.of("station_operators", "stations", "pos_devices",
			"agents");

	private static UUID companyA;
	private static UUID companyB;
	private static UUID operatorA;
	private static UUID operatorB;

	/** After the Spring context (and so Flyway) has started: the base class boots it first. */
	@BeforeEach
	void seed() {
		if (companyA != null) {
			return;
		}
		companyA = company();
		companyB = company();
		operatorA = operator();
		operatorB = operator();
	}

	@Test
	void withoutAScopeNothingIsVisible() throws SQLException {
		try (Connection connection = app(null, null)) {
			for (String table : concat()) {
				assertThat(count(connection, table)).as(table).isZero();
			}
		}
	}

	@Test
	void aCompanySeesItsOwnRowsOnly() throws SQLException {
		try (Connection connection = app("COMPANY", companyA)) {
			assertThat(count(connection, "companies where id = '" + companyB + "'")).isZero();
			assertThat(count(connection, "companies where id = '" + companyA + "'")).isEqualTo(1);
			assertThat(count(connection, "vehicles where company_id = '" + companyB + "'")).isZero();
			assertThat(count(connection, "vehicles where company_id = '" + companyA + "'")).isEqualTo(1);
			for (String table : OPERATOR_TABLES) {
				assertThat(count(connection, table)).as(table).isZero();
			}
		}
	}

	@Test
	void anOperatorSeesItsOwnRowsOnly() throws SQLException {
		try (Connection connection = app("OPERATOR", operatorA)) {
			assertThat(count(connection, "stations where operator_id = '" + operatorB + "'")).isZero();
			assertThat(count(connection, "stations where operator_id = '" + operatorA + "'")).isEqualTo(1);
			for (String table : COMPANY_TABLES) {
				assertThat(count(connection, table)).as(table).isZero();
			}
		}
	}

	@Test
	void aScopedUserCannotWriteMasterData() throws SQLException {
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("""
					insert into departments (id, company_id, name, status, created_at, updated_at)
					values ('%s', '%s', 'Sneaky', 'ACTIVE', now(), now())
					""".formatted(UUID.randomUUID(), companyA))).hasMessageContaining("row-level security");
		}
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			// Its own rows are visible, but no change to them passes the policy's check.
			assertThatThrownBy(
					() -> sql.executeUpdate("update vehicles set tier = 'VIP' where company_id = '" + companyA + "'"))
				.hasMessageContaining("row-level security");
		}
		try (Connection connection = app("COMPANY", companyA); Statement sql = connection.createStatement()) {
			// Another company's rows are not even found.
			assertThat(sql.executeUpdate("update vehicles set make = 'X' where company_id = '" + companyB + "'"))
				.isZero();
		}
	}

	@Test
	void theDatabaseRefusesAChildFromAnotherTenant() throws SQLException {
		UUID departmentOfB = Owner.queryOne("select id from departments where company_id = ?", companyB);
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("""
					update vehicles set department_id = '%s' where company_id = '%s'
					""".formatted(departmentOfB, companyA))).hasMessageContaining("is not in company");
		}
	}

	@Test
	void nothingCanBeDeletedAndPricesCannotBeEdited() throws SQLException {
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("delete from vehicles where company_id = '" + companyA + "'"))
				.hasMessageContaining("permission denied");
		}
		try (Connection connection = app("PLATFORM", null); Statement sql = connection.createStatement()) {
			assertThatThrownBy(() -> sql.executeUpdate("update fuel_prices set price_piastres = 1"))
				.hasMessageContaining("permission denied");
		}
	}

	// ----------------------------------------------------------------------------------- helpers

	/** A connection as the application has it: the runtime role, in a transaction with a scope. */
	private static Connection app(String scopeType, UUID scopeId) throws SQLException {
		Connection connection = Owner.connect();
		connection.setAutoCommit(false);
		try (Statement sql = connection.createStatement()) {
			sql.execute("SET ROLE zawed_app");
			if (scopeType != null) {
				sql.execute("select set_config('app.scope_type', '" + scopeType + "', true)");
			}
			if (scopeId != null) {
				sql.execute("select set_config('app.scope_id', '" + scopeId + "', true)");
			}
		}
		return connection;
	}

	private static long count(Connection connection, String from) throws SQLException {
		try (Statement sql = connection.createStatement(); ResultSet rs = sql.executeQuery("select count(*) from " + from)) {
			rs.next();
			return rs.getLong(1);
		}
	}

	private static List<String> concat() {
		return java.util.stream.Stream.concat(COMPANY_TABLES.stream(), OPERATOR_TABLES.stream()).toList();
	}

	private static UUID company() {
		UUID id = UUID.randomUUID();
		Owner.update("""
				insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes,
				                       low_balance_threshold_piastres, created_at, updated_at)
				values (?, 'Co', 'شركة', 'Co', 'ACTIVE', 180, 0, now(), now())
				""", id);
		UUID department = UUID.randomUUID();
		Owner.update("""
				insert into departments (id, company_id, name, status, created_at, updated_at)
				values (?, ?, 'Dept', 'ACTIVE', now(), now())
				""", department, id);
		UUID vehicle = UUID.randomUUID();
		String plate = "ISO " + vehicle.toString().substring(0, 8);
		Owner.update("""
				insert into vehicles (id, company_id, department_id, plate, plate_key, make, model, fuel_type,
				                      tank_capacity_ml, tier, status, created_at, updated_at)
				values (?, ?, ?, ?, ?, 'M', 'M', 'GAS_92', 50000, 'NORMAL', 'ACTIVE', now(), now())
				""", vehicle, id, department, plate, plate.replace(" ", "").toUpperCase());
		return id;
	}

	private static UUID operator() {
		UUID id = UUID.randomUUID();
		Owner.update("""
				insert into station_operators (id, legal_name, name_ar, name_en, status, created_at, updated_at)
				values (?, 'Op', 'مشغل', 'Op', 'ACTIVE', now(), now())
				""", id);
		Owner.update("""
				insert into stations (id, operator_id, name_ar, name_en, time_zone, connectivity, fuel_types, status,
				                      created_at, updated_at)
				values (?, ?, 'محطة', 'S', 'Africa/Cairo', 'NORMAL', '{GAS_92}', 'ACTIVE', now(), now())
				""", UUID.randomUUID(), id);
		return id;
	}

}
