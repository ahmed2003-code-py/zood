package com.zawed.api.leads;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.sql.Connection;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.time.Duration;
import java.time.Instant;
import java.util.UUID;

import javax.sql.DataSource;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;

import com.zawed.api.support.IntegrationTest;
import com.zawed.api.support.Owner;

/**
 * Leads at the database layer (V22), run the way the application runs (pooled connection, role
 * zawed_app): the public form's narrow door works without a scope, a lead is visible to a platform
 * user only (never to a company, an operator, or a POS route in the platform scope without a user),
 * the runtime role cannot insert, delete or change what the prospect sent, and closed leads are purged
 * after their retention and never inside 90 days.
 */
class LeadsIsolationTests extends IntegrationTest {

	@Autowired
	DataSource dataSource;

	@Autowired
	LeadRetention retention;

	@Test
	void theFormsDoorWorksWithoutAScopeAndOnlyAPlatformUserSeesTheLead() throws Exception {
		UUID lead = submitAsTheForm();
		UUID user = UUID.randomUUID();
		UUID tenant = UUID.randomUUID();

		assertThat(visible(null, null, null, lead)).as("no scope").isZero();
		assertThat(visible("PLATFORM", null, user, lead)).as("a platform user").isEqualTo(1);
		assertThat(visible("PLATFORM", null, null, lead)).as("the platform scope without a user (every POS route)").isZero();
		assertThat(visible("COMPANY", tenant, user, lead)).as("a company user").isZero();
		assertThat(visible("OPERATOR", tenant, user, lead)).as("a station manager").isZero();
	}

	@Test
	void theRuntimeRoleCannotInsertDeleteOrRewriteWhatTheProspectSent() throws Exception {
		UUID lead = submitAsTheForm();
		UUID user = UUID.randomUUID();
		String insert = "insert into leads (id, reference, company_name, contact_name, phone, email, fleet_size, locale, "
				+ "last_requested_at, created_at, updated_at) values ('" + UUID.randomUUID() + "', 'L-99999999', 'Xx', 'Xx', "
				+ "'01001234567', 'x@example.test', '1-19', 'ar', now(), now(), now())";
		for (String sql : new String[] { insert, "delete from leads where id = '" + lead + "'",
				"update leads set phone = '01009999999' where id = '" + lead + "'",
				"update leads set request_count = 99 where id = '" + lead + "'",
				"update leads set created_at = now() - interval '5 years' where id = '" + lead + "'" }) {
			try (Connection connection = scoped("PLATFORM", null, user); Statement statement = connection.createStatement()) {
				assertThatThrownBy(() -> statement.executeUpdate(sql)).as(sql)
					.isInstanceOf(SQLException.class)
					.hasMessageContaining("permission denied");
				connection.rollback();
			}
		}
	}

	@Test
	void onlyAPlatformUserMovesALeadAndOnlyInTheirOwnName() throws Exception {
		UUID lead = submitAsTheForm();
		UUID user = UUID.randomUUID();
		String move = "update leads set status = 'CONTACTED', updated_at = now(), updated_by = '%s' where id = '" + lead + "'";

		assertThat(update("COMPANY", UUID.randomUUID(), user, move.formatted(user))).as("a company").isZero();
		assertThat(update("OPERATOR", UUID.randomUUID(), user, move.formatted(user))).as("an operator").isZero();
		assertThat(update("PLATFORM", null, null, move.formatted(user))).as("a POS route").isZero();
		try (Connection connection = scoped("PLATFORM", null, user); Statement statement = connection.createStatement()) {
			assertThatThrownBy(() -> statement.executeUpdate(move.formatted(UUID.randomUUID())))
				.as("in another user's name")
				.hasMessageContaining("row-level security");
			connection.rollback();
		}
		assertThat(update("PLATFORM", null, user, move.formatted(user))).as("a platform user").isEqualTo(1);
	}

	@Test
	void theFormsDoorWritesOnlyValidValuesAndAlwaysANewLead() throws Exception {
		try (Connection connection = dataSource.getConnection(); PreparedStatement call = connection
			.prepareStatement("select * from submit_lead(?, 'Nile Co', 'Mona', '123', 'm@example.test', '1-19', 'ar')")) {
			call.setObject(1, UUID.randomUUID());
			assertThatThrownBy(call::executeQuery).hasMessageContaining("check constraint");
		}
		UUID lead = submitAsTheForm();
		assertThat(Owner.<String>queryOne("select status || ' ' || coalesce(note, '-') || ' ' || request_count "
				+ "|| ' ' || (updated_by is null) || ' ' || (closed_at is null) from leads where id = ?", lead))
			.isEqualTo("NEW - 1 true true");
	}

	@Test
	void closedLeadsArePurgedAfterTheirRetentionAndNothingElse() throws Exception {
		Instant now = Instant.now();
		UUID oldClosed = LeadsOpsTests.plant(now.minus(Duration.ofDays(900)), "CLOSED");
		Owner.update("update leads set closed_at = now() - interval '800 days' where id = ?", oldClosed);
		UUID recentlyClosed = LeadsOpsTests.plant(now.minus(Duration.ofDays(900)), "CLOSED");
		Owner.update("update leads set closed_at = now() - interval '100 days' where id = ?", recentlyClosed);
		UUID oldOpen = LeadsOpsTests.plant(now.minus(Duration.ofDays(900)), "QUALIFIED");

		assertThat(retention.purge()).isGreaterThanOrEqualTo(1);
		assertThat(Owner.<Long>queryOne("select count(*) from leads where id = ?", oldClosed)).isZero();
		assertThat(Owner.<Long>queryOne("select count(*) from leads where id in (?, ?)", recentlyClosed, oldOpen))
			.isEqualTo(2L);
		assertThat(LeadRetention.effective(Duration.ofDays(1))).isEqualTo(Duration.ofDays(91));
		assertThat(LeadRetention.effective(Duration.ofDays(730))).isEqualTo(Duration.ofDays(730));

		for (String cutoff : new String[] { "now() - interval '30 days'", "now() - interval '89 days'", "null" }) {
			try (Connection connection = dataSource.getConnection(); Statement statement = connection.createStatement()) {
				assertThatThrownBy(() -> statement.executeQuery("select purge_closed_leads(" + cutoff + ", 100)"))
					.as(cutoff)
					.hasMessageContaining("90-day minimum");
			}
		}
		assertThat(Owner.<Long>queryOne("select count(*) from leads where id = ?", recentlyClosed)).isEqualTo(1L);
	}

	// ------------------------------------------------------------------------------------ helpers

	/** What the public form does: the definer function, on the application's role, with no scope. */
	private UUID submitAsTheForm() throws SQLException {
		String token = UUID.randomUUID().toString().substring(0, 8);
		try (Connection connection = dataSource.getConnection(); PreparedStatement call = connection.prepareStatement(
				"select lead_id, repeated from submit_lead(?, ?, 'Mona Adel', ?, ?, '20-49', 'ar')")) {
			call.setObject(1, UUID.randomUUID());
			call.setString(2, "Isolation Co " + token);
			call.setString(3, "011" + String.format("%08d", Math.floorMod(token.hashCode(), 100_000_000)));
			call.setString(4, "iso-" + token + "@example.test");
			try (ResultSet rs = call.executeQuery()) {
				rs.next();
				assertThat(rs.getBoolean(2)).as("a new lead").isFalse();
				return rs.getObject(1, UUID.class);
			}
		}
	}

	private Connection scoped(String type, UUID id, UUID user) throws SQLException {
		Connection connection = dataSource.getConnection();
		connection.setAutoCommit(false);
		try (PreparedStatement statement = connection.prepareStatement("select set_config('app.scope_type', ?, true), "
				+ "set_config('app.scope_id', ?, true), set_config('app.user_id', ?, true)")) {
			statement.setString(1, type == null ? "" : type);
			statement.setString(2, id == null ? "" : id.toString());
			statement.setString(3, user == null ? "" : user.toString());
			statement.execute();
		}
		return connection;
	}

	private int visible(String type, UUID id, UUID user, UUID lead) throws SQLException {
		try (Connection connection = scoped(type, id, user);
				PreparedStatement statement = connection.prepareStatement("select count(*) from leads where id = ?")) {
			statement.setObject(1, lead);
			try (ResultSet rs = statement.executeQuery()) {
				rs.next();
				int count = rs.getInt(1);
				connection.rollback();
				return count;
			}
		}
	}

	private int update(String type, UUID id, UUID user, String sql) throws SQLException {
		try (Connection connection = scoped(type, id, user); Statement statement = connection.createStatement()) {
			int count = statement.executeUpdate(sql);
			connection.rollback();
			return count;
		}
	}

}
