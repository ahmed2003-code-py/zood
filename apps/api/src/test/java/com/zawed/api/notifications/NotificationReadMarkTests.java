package com.zawed.api.notifications;

import static com.zawed.api.support.Notified.list;
import static com.zawed.api.support.Notified.unread;
import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.net.http.HttpResponse;
import java.sql.Connection;
import java.sql.PreparedStatement;
import java.sql.SQLException;
import java.sql.Timestamp;
import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import javax.sql.DataSource;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * Read state by a per-user mark (B7 W5 review, M2) and the feed's keyset cursor (L4): "mark all
 * read" writes one mark, not a row per notification; the unread count ranges over what is newer
 * than the mark through the audience index; a notification still being committed when the user
 * marks all stays unread; concurrent clicks only move the mark forward; and paging by
 * {@code (created_at, id)} neither skips nor repeats rows that share a time.
 */
class NotificationReadMarkTests extends IdentityTestSupport {

	@Autowired
	DataSource dataSource;

	@Autowired
	TransactionTemplate transactions;

	@Autowired
	JdbcClient jdbc;

	// ------------------------------------------------------------------------------------- M2

	@Test
	void markingAllReadWritesAMarkNotARowPerNotification() throws Exception {
		Scope company = Scope.company(seedCompany());
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, company, false));
		for (int i = 0; i < 30; i++) {
			plant(company, Instant.now().minus(2, ChronoUnit.HOURS).plusSeconds(i));
		}
		UUID recent = plant(company, Instant.now());
		assertThat(unread(this, fleet)).isEqualTo(31);

		assertThat(authed("POST", "/api/v1/notifications/read-all", fleet.accessToken(), null).statusCode()).isEqualTo(204);

		assertThat(unread(this, fleet)).isZero();
		assertThat(list(this, fleet, true)).isEmpty();
		assertThat(list(this, fleet, false)).hasSize(31).allSatisfy((n) -> {
			assertThat(n.get("read")).isEqualTo(true);
			assertThat(n.get("readAt")).isNotNull();
		});
		assertThat(Owner.<Long>queryOne("select count(*) from notification_read_marks where user_id = ?", fleet.userId()))
			.isEqualTo(1L);
		// Only what the grace window holds is marked row by row: here the one recent notice.
		assertThat(Owner.<UUID>queryList("select notification_id from notification_reads where user_id = ?",
				fleet.userId()))
			.containsExactly(recent);

		UUID later = plant(company, Instant.now());
		assertThat(unread(this, fleet)).isEqualTo(1);
		assertThat(list(this, fleet, true)).extracting((n) -> n.get("id")).containsExactly(later.toString());
		assertThat(authed("POST", "/api/v1/notifications/" + later + "/read", fleet.accessToken(), null).statusCode())
			.isEqualTo(204);
		assertThat(unread(this, fleet)).isZero();
	}

	/**
	 * The race the grace window is for: a notification inserted before the click and committed after
	 * it was never seen, so it must stay unread. A mark at the moment of the click would cover it.
	 */
	@Test
	void aNotificationCommittedWhileTheUserMarksAllReadStaysUnread() throws Exception {
		Scope company = Scope.company(seedCompany());
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, company, false));
		UUID answeredBy = seed(Role.OPS).id();
		plant(company, Instant.now().minus(1, ChronoUnit.HOURS));
		UUID late = UUID.randomUUID();

		try (Connection open = scoped(Scope.PLATFORM, answeredBy)) {
			// Inserted (and stamped) now, committed only after the click below.
			try (PreparedStatement statement = open.prepareStatement("""
					insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles,
					                           entity_type, entity_id, params, actor_id, created_at)
					values (?, 'TICKET_ANSWERED', 'COMPANY', ?, '{FLEET_MANAGER}', 'TICKET', gen_random_uuid(), '{}',
					        app_user_id(), clock_timestamp())
					""")) {
				statement.setObject(1, late);
				statement.setObject(2, company.id());
				statement.executeUpdate();
			}
			HttpResponse<String> all = authed("POST", "/api/v1/notifications/read-all", fleet.accessToken(), null);
			assertThat(all.statusCode()).as(all.body()).isEqualTo(204);
			assertThat(unread(this, fleet)).isZero();
			open.commit();
		}

		assertThat(unread(this, fleet)).isEqualTo(1);
		assertThat(list(this, fleet, true)).extracting((n) -> n.get("id")).containsExactly(late.toString());
	}

	@Test
	void concurrentMarkAllClicksOnlyMoveTheMarkForward() throws Exception {
		Scope company = Scope.company(seedCompany());
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, company, false));
		for (int i = 0; i < 5; i++) {
			plant(company, Instant.now().minusSeconds(i));
		}
		List<Integer> statuses = Concurrently.run(8,
				(i) -> authed("POST", "/api/v1/notifications/read-all", fleet.accessToken(), null).statusCode());
		assertThat(statuses).containsOnly(204);
		assertThat(unread(this, fleet)).isZero();
		assertThat(Owner.<Long>queryOne("select count(*) from notification_read_marks where user_id = ?", fleet.userId()))
			.isEqualTo(1L);
		assertThat(Owner.<Long>queryOne("select count(*) from notification_reads where user_id = ?", fleet.userId()))
			.as("one row per notification, however many clicks")
			.isEqualTo(5L);

		Timestamp mark = Owner.queryOne("select read_up_to from notification_read_marks where user_id = ?",
				fleet.userId());
		assertThat(authed("POST", "/api/v1/notifications/read-all", fleet.accessToken(), null).statusCode()).isEqualTo(204);
		Timestamp next = Owner.queryOne("select read_up_to from notification_read_marks where user_id = ?",
				fleet.userId());
		assertThat(next).isAfterOrEqualTo(mark);
	}

	@Test
	void aMarkIsItsOwnUsersAndNeverMovesBack() throws Exception {
		Scope company = Scope.company(seedCompany());
		SeededUser fleet = seed(Role.FLEET_MANAGER, company, false);
		SeededUser accountant = seed(Role.ACCOUNTANT, company, false);
		Owner.update("insert into notification_read_marks (user_id, read_up_to, marked_at) values (?, now(), now())",
				accountant.id());

		try (Connection c = scoped(company, fleet.id());
				var rs = c.createStatement().executeQuery("select count(*) from notification_read_marks")) {
			rs.next();
			assertThat(rs.getLong(1)).as("someone else's mark is invisible").isZero();
			c.rollback();
		}
		try (Connection c = scoped(company, fleet.id())) {
			assertThatThrownBy(() -> c.createStatement()
				.executeUpdate("insert into notification_read_marks (user_id, read_up_to, marked_at) values ('"
						+ accountant.id() + "', now(), now())"))
				.hasMessageContaining("row-level security");
			c.rollback();
		}
		try (Connection c = scoped(company, accountant.id())) {
			assertThatThrownBy(() -> c.createStatement()
				.executeUpdate("update notification_read_marks set read_up_to = read_up_to - interval '1 day'"))
				.hasMessageContaining("only moves forward");
			c.rollback();
		}
		try (Connection c = scoped(company, accountant.id())) {
			assertThatThrownBy(() -> c.createStatement().executeUpdate("delete from notification_read_marks"))
				.hasMessageContaining("permission denied");
			c.rollback();
		}
		try (Connection c = scoped(company, fleet.id())) {
			assertThat(c.createStatement()
				.executeUpdate("update notification_read_marks set read_up_to = read_up_to + interval '1 day'"))
				.as("another user's mark cannot be moved either")
				.isZero();
			c.rollback();
		}
	}

	/**
	 * The unread count is an index range from the mark onward, so Operations' count reads what
	 * arrived since they last marked all read, not the platform's whole feed; the feed's page is a
	 * row comparison on the audience index. Checked on the application's own statements, as Ops, with
	 * row-level security on. (A bound that stayed a filter would scan the feed: the first version of
	 * this fix, with the mark inline under coalesce, did exactly that under row-level security.)
	 */
	@Test
	void theUnreadCountAndTheFeedAreRangesOnTheAudienceIndex() {
		SeededUser opsUser = seed(Role.OPS);
		ZawedPrincipal ops = new ZawedPrincipal(opsUser.id(), UUID.randomUUID(), Role.OPS, Scope.PLATFORM, false);

		// Either index bounds it by the mark: the audience one, or the purge's (created_at) one when
		// the planner judges the platform audience to be most of the table.
		List<String> unreadPlan = plan(ops, Notifications.unreadSql(ops),
				Map.of("cap", 100, "mark", Timestamp.from(Instant.now().minus(1, ChronoUnit.HOURS))));
		assertThat(unreadPlan).anySatisfy((line) -> assertThat(line).containsAnyOf("notifications_audience_idx",
				"notifications_created_at_idx"));
		assertThat(unreadPlan).anySatisfy((line) -> assertThat(line).contains("Index Cond").contains("created_at >"));

		List<String> pagePlan = plan(ops, Notifications.feedSql(ops, false, true), Map.of("limit", 26, "cursorAt",
				Timestamp.from(Instant.now()), "cursorId", UUID.randomUUID()));
		assertThat(pagePlan).anySatisfy((line) -> assertThat(line).contains("notifications_audience_idx"));
		assertThat(pagePlan)
			.anySatisfy((line) -> assertThat(line).contains("Index Cond").contains("ROW(").contains("created_at"));

		List<String> unreadPagePlan = plan(ops, Notifications.feedSql(ops, true, false),
				Map.of("limit", 26, "mark", Timestamp.from(Instant.now().minus(1, ChronoUnit.HOURS))));
		assertThat(unreadPagePlan).anySatisfy((line) -> assertThat(line).contains("Index Cond").contains("created_at"));
	}

	// ------------------------------------------------------------------------------------- L4

	@Test
	void pagingByTimeAndIdNeitherSkipsNorRepeatsRowsThatShareATime() throws Exception {
		Scope company = Scope.company(seedCompany());
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, company, false));
		Instant tie = Instant.now().minus(1, ChronoUnit.HOURS).truncatedTo(ChronoUnit.MICROS);
		List<Planted> planted = new ArrayList<>();
		for (int i = 0; i < 7; i++) {
			// Random ids, not time-ordered ones, so id order says nothing about time.
			planted.add(new Planted(plant(company, tie), tie));
		}
		planted.add(new Planted(plant(company, tie.plusSeconds(1)), tie.plusSeconds(1)));
		planted.add(new Planted(plant(company, tie.minusSeconds(1)), tie.minusSeconds(1)));
		List<String> expected = planted.stream()
			.sorted(Comparator.comparing(Planted::createdAt).thenComparing((p) -> p.id().toString()).reversed())
			.map((p) -> p.id().toString())
			.toList();

		List<String> seen = new ArrayList<>();
		String cursor = null;
		int pages = 0;
		do {
			HttpResponse<String> page = authed("GET",
					"/api/v1/notifications?limit=2" + (cursor == null ? "" : "&cursor=" + cursor), fleet.accessToken(), null);
			assertThat(page.statusCode()).as(page.body()).isEqualTo(200);
			seen.addAll(JsonPath.read(page.body(), "$.items[*].id"));
			cursor = JsonPath.read(page.body(), "$.nextCursor");
			pages++;
		}
		while (cursor != null && pages < 20);

		assertThat(seen).containsExactlyElementsOf(expected);
		assertThat(pages).isEqualTo(5);
	}

	@Test
	void aCursorThatIsNotAKeysetIsABadRequest() throws Exception {
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(seedCompany()), false));
		for (String bad : List.of(UUID.randomUUID().toString(), "12.nope", "x." + UUID.randomUUID(),
				"99999999999999999999." + UUID.randomUUID())) {
			HttpResponse<String> response = authed("GET", "/api/v1/notifications?cursor=" + bad, fleet.accessToken(), null);
			assertThat(response.statusCode()).as(bad).isEqualTo(400);
			assertThat(code(response)).isEqualTo("INVALID_CURSOR");
		}
	}

	// ---------------------------------------------------------------------------------- helpers

	private record Planted(UUID id, Instant createdAt) {
	}

	/** The statement's plan as {@code me}, in their scope, with sequential scans off (tables are small here). */
	private List<String> plan(ZawedPrincipal me, String sql, Map<String, Object> params) {
		return DbScope.runAs(new DbScope.Context(me.scope(), me.userId()), () -> transactions.execute((status) -> {
			jdbc.sql("set local enable_seqscan = off").update();
			var statement = Notifications.audience(me, jdbc.sql("explain " + sql));
			for (Map.Entry<String, Object> param : params.entrySet()) {
				statement = statement.param(param.getKey(), param.getValue());
			}
			return statement.query(String.class).list();
		}));
	}

	/** A notification for the company's fleet managers, written as the owner, at a given time. */
	private static UUID plant(Scope company, Instant createdAt) {
		UUID id = UUID.randomUUID();
		Owner.update("""
				insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
				                           entity_id, params, actor_id, created_at)
				values (?, 'TICKET_ANSWERED', 'COMPANY', ?, '{FLEET_MANAGER}', 'TICKET', gen_random_uuid(), '{}', null, ?)
				""", id, company.id(), Timestamp.from(createdAt));
		return id;
	}

	/** A pooled connection, as the application uses it, in a scope and as a user. */
	private Connection scoped(Scope scope, UUID userId) throws SQLException {
		Connection connection = dataSource.getConnection();
		connection.setAutoCommit(false);
		try (PreparedStatement statement = connection.prepareStatement("select set_config('app.scope_type', ?, true), "
				+ "set_config('app.scope_id', ?, true), set_config('app.user_id', ?, true)")) {
			statement.setString(1, scope.type().name());
			statement.setString(2, scope.id() == null ? "" : scope.id().toString());
			statement.setString(3, userId == null ? "" : userId.toString());
			statement.execute();
		}
		return connection;
	}

}
