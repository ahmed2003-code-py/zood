package com.zawed.api.notifications;

import static com.zawed.api.support.Notified.about;
import static com.zawed.api.support.Notified.list;
import static com.zawed.api.support.Notified.params;
import static com.zawed.api.support.Notified.typesAbout;
import static com.zawed.api.support.Notified.unread;
import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.sql.Connection;
import java.sql.PreparedStatement;
import java.sql.SQLException;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import javax.sql.DataSource;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.context.ApplicationEventPublisher;
import org.springframework.transaction.IllegalTransactionStateException;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.ratelimit.InMemoryRateLimiter;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.tickets.TicketAnswered;
import com.zawed.api.support.Owner;

/**
 * In-app notifications (B7 W5): each event reaches its audience (scope and roles) and nobody else,
 * a notification exists only if its cause committed, read state belongs to each user, and the
 * database refuses what the application must never do (another tenant's feed, free text, edits).
 * The fueling and station-finance causes are covered beside their own test support
 * ({@code FuelingNotificationsTests}, {@code StationFinanceNotificationsTests}).
 */
class NotificationsTests extends IdentityTestSupport {

	@Autowired
	DataSource dataSource;

	@Autowired
	TransactionTemplate transactions;

	@Autowired
	ApplicationEventPublisher events;

	@Autowired
	NotificationRetention retention;

	@Autowired
	InMemoryRateLimiter rateLimits;

	private SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	// ------------------------------------------------------------------------------------ tickets

	@Test
	void aTicketOpenedByACompanyReachesOperationsAndNobodyElse() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = companyUser(company, Role.FLEET_MANAGER);
		SignedIn accountant = companyUser(company, Role.ACCOUNTANT);
		SignedIn otherFleet = companyUser(seedCompany(), Role.FLEET_MANAGER);
		SignedIn opsManager = signIn(seed(Role.OPS_MANAGER));
		SignedIn superAdmin = signIn(seed(Role.SUPER_ADMIN));
		rateLimits.reset();
		SignedIn stationManager = signIn(seed(Role.STATION_MANAGER));

		String ticket = openCompanyTicket(fleet);

		Map<String, Object> seen = about(this, ops, ticket, "TICKET_OPENED");
		assertThat(seen.get("entityType")).isEqualTo("TICKET");
		assertThat(seen.get("read")).isEqualTo(false);
		assertThat(params(seen)).containsEntry("ticketType", "LOST_TAG").containsEntry("companyId", company.toString());
		assertThat(seen.toString()).as("no subject or body is copied").doesNotContain("Tag lost", "fell off");
		assertThat(typesAbout(this, opsManager, ticket)).containsExactly("TICKET_OPENED");
		assertThat(typesAbout(this, superAdmin, ticket)).as("the audience is Ops and Ops Manager").isEmpty();
		for (SignedIn outsider : List.of(fleet, accountant, otherFleet, stationManager)) {
			assertThat(unread(this, outsider)).isZero();
			assertThat(list(this, outsider, false)).isEmpty();
		}
	}

	@Test
	void operationsAnsweringATicketTellsTheCompanyAndAnInternalNoteDoesNot() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = companyUser(company, Role.FLEET_MANAGER);
		SignedIn accountant = companyUser(company, Role.ACCOUNTANT);
		SignedIn otherFleet = companyUser(seedCompany(), Role.FLEET_MANAGER);
		String ticket = openCompanyTicket(fleet);

		reply(ticket, "{\"body\":\"Check the tag stock first\",\"internal\":true}");
		assertThat(unread(this, fleet)).as("an internal note is not an answer").isZero();

		reply(ticket, "{\"body\":\"A new tag is on its way.\"}");
		for (SignedIn member : List.of(fleet, accountant)) {
			Map<String, Object> answered = about(this, member, ticket, "TICKET_ANSWERED");
			assertThat(params(answered)).containsEntry("ticketType", "LOST_TAG");
			assertThat(answered.toString()).doesNotContain("new tag");
			assertThat(unread(this, member)).isEqualTo(1);
		}
		assertThat(unread(this, otherFleet)).isZero();
		assertThat(typesAbout(this, ops, ticket)).as("Operations wrote it").containsExactly("TICKET_OPENED");
	}

	@Test
	void aStationTicketReachesOperationsAndItsAnswerOnlyThatOperator() throws Exception {
		String operator = operator();
		SignedIn manager = stationManager(operator);
		SignedIn otherManager = stationManager(operator());
		SignedIn fleet = companyUser(seedCompany(), Role.FLEET_MANAGER);

		HttpResponse<String> opened = authed("POST", "/api/v1/station/tickets", manager.accessToken(),
				"{\"type\":\"DEVICE_FAULT\",\"subject\":\"Reader dead\",\"body\":\"Pump 3 does not read tags.\"}");
		assertThat(opened.statusCode()).as(opened.body()).isEqualTo(201);
		String ticket = JsonPath.read(opened.body(), "$.ticket.id");
		assertThat(params(about(this, ops, ticket, "TICKET_OPENED"))).containsEntry("operatorId", operator)
			.containsEntry("ticketType", "DEVICE_FAULT");

		reply(ticket, "{\"body\":\"A technician comes tomorrow.\"}");
		assertThat(typesAbout(this, manager, ticket)).containsExactly("TICKET_ANSWERED");
		assertThat(unread(this, otherManager)).isZero();
		assertThat(unread(this, fleet)).isZero();
	}

	// ------------------------------------------------------------------------------ wallet money

	@Test
	void aTopUpTellsTheApproversWhenVerifiedAndTheCompanyWhenCredited() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = companyUser(company, Role.FLEET_MANAGER);
		SignedIn accountant = companyUser(company, Role.ACCOUNTANT);
		SignedIn otherFleet = companyUser(seedCompany(), Role.FLEET_MANAGER);
		SignedIn verifier = signIn(seed(Role.OPS_MANAGER));
		SignedIn approver = signIn(seed(Role.OPS_MANAGER));

		String topUp = requestTopUp(fleet, 250_000);
		assertThat(typesAbout(this, approver, topUp)).as("a claim is not yet anything to approve").isEmpty();

		HttpResponse<String> verified = authed("POST", "/api/v1/ops/topups/" + topUp + "/verify", verifier.accessToken(),
				"{\"receivedAmountPiastres\":240000}");
		assertThat(verified.statusCode()).as(verified.body()).isEqualTo(200);
		Map<String, Object> waiting = about(this, approver, topUp, "TOPUP_AWAITING_APPROVAL");
		assertThat(waiting.get("entityType")).isEqualTo("TOPUP");
		assertThat(params(waiting)).containsEntry("companyId", company.toString()).containsEntry("amountPiastres", 240_000);
		assertThat(typesAbout(this, verifier, topUp)).as("the verifier cannot approve it: not told").isEmpty();
		assertThat(typesAbout(this, ops, topUp)).as("plain Ops cannot approve").isEmpty();
		assertThat(unread(this, fleet)).isZero();

		HttpResponse<String> approved = authed("POST", "/api/v1/ops/topups/" + topUp + "/approve", approver.accessToken(),
				null);
		assertThat(approved.statusCode()).as(approved.body()).isEqualTo(200);
		for (SignedIn member : List.of(fleet, accountant)) {
			Map<String, Object> credited = about(this, member, topUp, "TOPUP_CREDITED");
			assertThat(params(credited)).containsEntry("amountPiastres", 240_000).containsKey("transactionId");
		}
		assertThat(unread(this, otherFleet)).isZero();
		assertThat(typesAbout(this, approver, topUp)).containsExactly("TOPUP_AWAITING_APPROVAL");

		// A repeated approval posts nothing and tells nobody again.
		authed("POST", "/api/v1/ops/topups/" + topUp + "/approve", approver.accessToken(), null);
		assertThat(typesAbout(this, fleet, topUp)).containsExactly("TOPUP_CREDITED");
	}

	@Test
	void aRejectedTopUpRequestTellsTheCompanyWithoutTheReason() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = companyUser(company, Role.FLEET_MANAGER);
		String topUp = requestTopUp(fleet, 50_000);

		HttpResponse<String> rejected = authed("POST", "/api/v1/ops/topups/" + topUp + "/reject", ops.accessToken(),
				"{\"reason\":\"No such transfer on the statement\"}");
		assertThat(rejected.statusCode()).as(rejected.body()).isEqualTo(200);
		Map<String, Object> told = about(this, fleet, topUp, "TOPUP_REJECTED");
		assertThat(params(told)).containsEntry("amountPiastres", 50_000);
		assertThat(told.toString()).doesNotContain("statement");
	}

	@Test
	void aWalletAdjustmentWaitsForAnApproverAndARepeatedRequestTellsThemOnce() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = companyUser(company, Role.FLEET_MANAGER);
		SignedIn opsManager = signIn(seed(Role.OPS_MANAGER));
		String key = "adj-" + UUID.randomUUID();
		String body = "{\"amountPiastres\":7000,\"reason\":\"Goodwill after a pump fault\"}";

		String adjustment = null;
		for (int attempt = 0; attempt < 2; attempt++) {
			HttpResponse<String> asked = send(request("/api/v1/ops/companies/" + company + "/wallet/adjustments")
				.header("Authorization", "Bearer " + ops.accessToken())
				.header("Content-Type", "application/json")
				.header("Idempotency-Key", key)
				.POST(HttpRequest.BodyPublishers.ofString(body)));
			assertThat(asked.statusCode()).as(asked.body()).isIn(200, 201);
			String id = JsonPath.read(asked.body(), "$.id");
			assertThat(adjustment == null || adjustment.equals(id)).isTrue();
			adjustment = id;
		}

		Map<String, Object> waiting = about(this, opsManager, adjustment, "WALLET_ADJUSTMENT_AWAITING_APPROVAL");
		assertThat(params(waiting)).containsEntry("companyId", company.toString()).containsEntry("amountPiastres", 7_000);
		assertThat(typesAbout(this, ops, adjustment)).isEmpty();
		assertThat(unread(this, fleet)).isZero();
	}

	// ------------------------------------------------------------------------------- read state

	@Test
	void readStateBelongsToEachUser() throws Exception {
		UUID company = seedCompany();
		SignedIn first = companyUser(company, Role.FLEET_MANAGER);
		SignedIn second = companyUser(company, Role.FLEET_MANAGER);
		SignedIn accountant = companyUser(company, Role.ACCOUNTANT);
		SignedIn otherFleet = companyUser(seedCompany(), Role.FLEET_MANAGER);
		String ticket = openCompanyTicket(first);
		reply(ticket, "{\"body\":\"First answer\"}");
		reply(ticket, "{\"body\":\"Second answer\"}");

		List<Map<String, Object>> mine = list(this, first, false);
		assertThat(mine).hasSize(2);
		String newest = (String) mine.get(0).get("id");
		String oldest = (String) mine.get(1).get("id");
		assertThat(newest).isGreaterThan(oldest);
		assertThat(unread(this, first)).isEqualTo(2);

		for (int i = 0; i < 2; i++) { // marking twice is the same as once
			HttpResponse<String> marked = authed("POST", "/api/v1/notifications/" + oldest + "/read", first.accessToken(),
					null);
			assertThat(marked.statusCode()).as(marked.body()).isEqualTo(204);
		}
		assertThat(unread(this, first)).isEqualTo(1);
		assertThat(list(this, first, true)).extracting((n) -> n.get("id")).containsExactly(newest);
		Map<String, Object> read = list(this, first, false).get(1);
		assertThat(read.get("read")).isEqualTo(true);
		assertThat(read.get("readAt")).isNotNull();
		assertThat(unread(this, second)).as("the other fleet manager has read nothing").isEqualTo(2);

		HttpResponse<String> all = authed("POST", "/api/v1/notifications/read-all", second.accessToken(), null);
		assertThat(all.statusCode()).as(all.body()).isEqualTo(204);
		assertThat(unread(this, second)).isZero();
		assertThat(unread(this, first)).isEqualTo(1);
		assertThat(unread(this, accountant)).isEqualTo(2);

		// Another company's user, or Operations, cannot touch it: it does not exist for them.
		assertThat(authed("POST", "/api/v1/notifications/" + newest + "/read", otherFleet.accessToken(), null).statusCode())
			.isEqualTo(404);
		assertThat(authed("POST", "/api/v1/notifications/" + newest + "/read", ops.accessToken(), null).statusCode())
			.isEqualTo(404);
		assertThat(unread(this, first)).isEqualTo(1);
		assertThat(Owner.<Long>queryOne("select count(*) from notification_reads where notification_id = ?::uuid", newest))
			.isEqualTo(1L);
	}

	@Test
	void aBadCursorIsABadRequest() throws Exception {
		HttpResponse<String> response = authed("GET", "/api/v1/notifications?cursor=nope", ops.accessToken(), null);
		assertThat(response.statusCode()).isEqualTo(400);
		assertThat(code(response)).isEqualTo("INVALID_CURSOR");
	}

	// ---------------------------------------------------------------- written with the cause only

	@Test
	void aCauseThatRollsBackLeavesNoNotification() {
		UUID company = seedCompany();
		UUID rolledBack = UUID.randomUUID();
		UUID failed = UUID.randomUUID();
		UUID committed = UUID.randomUUID();

		asPlatform(() -> transactions.executeWithoutResult((status) -> {
			events.publishEvent(new TicketAnswered(rolledBack, "OTHER", company, null));
			status.setRollbackOnly();
		}));
		assertThatThrownBy(() -> asPlatform(() -> transactions.executeWithoutResult((status) -> {
			events.publishEvent(new TicketAnswered(failed, "OTHER", company, null));
			throw new IllegalStateException("the cause failed after announcing itself");
		}))).hasMessageContaining("the cause failed");
		asPlatform(() -> transactions.executeWithoutResult(
				(status) -> events.publishEvent(new TicketAnswered(committed, "OTHER", company, null))));

		assertThat(notificationsAbout(rolledBack)).isZero();
		assertThat(notificationsAbout(failed)).isZero();
		assertThat(notificationsAbout(committed)).isEqualTo(1);
	}

	@Test
	void anEventOutsideATransactionIsRefusedRatherThanWrittenUnscoped() {
		UUID ticket = UUID.randomUUID();
		assertThatThrownBy(() -> events.publishEvent(new TicketAnswered(ticket, "OTHER", seedCompany(), null)))
			.isInstanceOf(IllegalTransactionStateException.class);
		assertThat(notificationsAbout(ticket)).isZero();
	}

	// ------------------------------------------------------------------ the database's own rules

	@Test
	void rowLevelSecurityShowsEachUserOnlyTheirScopeAndRole() throws Exception {
		Scope companyA = Scope.company(seedCompany());
		Scope companyB = Scope.company(seedCompany());
		SeededUser fleetA = seed(Role.FLEET_MANAGER, companyA, false);
		SeededUser accountantA = seed(Role.ACCOUNTANT, companyA, false);
		SeededUser fleetB = seed(Role.FLEET_MANAGER, companyB, false);
		SeededUser opsUser = seed(Role.OPS);
		SeededUser opsManager = seed(Role.OPS_MANAGER);
		UUID forFleetA = plant(companyA, "{FLEET_MANAGER}", null);
		UUID forCompanyB = plant(companyB, "{FLEET_MANAGER,ACCOUNTANT}", null);
		UUID forApprovers = plant(Scope.PLATFORM, "{OPS_MANAGER}", null);
		UUID causedByFleetA = plant(companyA, "{FLEET_MANAGER}", fleetA.id());
		List<UUID> planted = List.of(forFleetA, forCompanyB, forApprovers, causedByFleetA);

		assertThat(visible(companyA, fleetA.id(), planted)).containsExactly(forFleetA);
		assertThat(visible(companyA, accountantA.id(), planted)).as("not addressed to accountants").isEmpty();
		assertThat(visible(companyB, fleetB.id(), planted)).containsExactly(forCompanyB);
		assertThat(visible(Scope.PLATFORM, opsUser.id(), planted)).as("the platform does not read company feeds")
			.isEmpty();
		assertThat(visible(Scope.PLATFORM, opsManager.id(), planted)).containsExactly(forApprovers);
		assertThat(visible(companyA, null, planted)).as("no user, no role, nothing").isEmpty();
		// A user's role comes from their row: another company's user id in this scope has no role here.
		assertThat(visible(companyA, fleetB.id(), planted)).isEmpty();
	}

	@Test
	void theApplicationCannotWriteIntoAnotherTenantsFeedNorChangeOne() throws Exception {
		Scope companyA = Scope.company(seedCompany());
		Scope companyB = Scope.company(seedCompany());
		SeededUser fleetA = seed(Role.FLEET_MANAGER, companyA, false);
		SeededUser accountantA = seed(Role.ACCOUNTANT, companyA, false);
		UUID forCompanyB = plant(companyB, "{FLEET_MANAGER}", null);
		UUID forFleetA = plant(companyA, "{FLEET_MANAGER}", null);

		// Each refusal aborts its transaction, so each gets its own.
		try (Connection c = scoped(companyA, fleetA.id())) {
			assertThatThrownBy(() -> insert(c, companyB, "app_user_id()")).hasMessageContaining("row-level security");
			c.rollback();
		}
		try (Connection c = scoped(companyA, fleetA.id())) {
			assertThatThrownBy(() -> insert(c, Scope.PLATFORM, "'" + accountantA.id() + "'"))
				.as("the actor is the transaction's user, never another")
				.hasMessageContaining("row-level security");
			c.rollback();
		}
		try (Connection c = scoped(companyA, fleetA.id())) {
			insert(c, Scope.PLATFORM, "app_user_id()"); // a ticket it opened: Operations is told
			insert(c, companyA, "app_user_id()");
			c.rollback();
		}
		try (Connection c = scoped(companyA, null)) {
			assertThatThrownBy(() -> insert(c, companyA, "null")).as("a user-less company transaction writes nothing")
				.hasMessageContaining("row-level security");
			c.rollback();
		}
		try (Connection c = dataSource.getConnection()) {
			c.setAutoCommit(false);
			assertThatThrownBy(() -> insert(c, Scope.PLATFORM, "null")).as("no scope, nothing")
				.hasMessageContaining("row-level security");
			c.rollback();
		}
		for (String change : List.of("update notifications set params = '{}'", "delete from notifications",
				"update notification_reads set read_at = now()", "delete from notification_reads")) {
			try (Connection c = scoped(Scope.PLATFORM, null)) {
				assertThatThrownBy(() -> c.createStatement().executeUpdate(change)).as(change)
					.hasMessageContaining("permission denied");
				c.rollback();
			}
		}
		try (Connection c = scoped(companyA, fleetA.id())) {
			assertThatThrownBy(() -> markRead(c, accountantA.id(), forFleetA)).as("someone else's read state")
				.hasMessageContaining("row-level security");
			c.rollback();
		}
		try (Connection c = scoped(companyA, fleetA.id())) {
			assertThatThrownBy(() -> markRead(c, fleetA.id(), forCompanyB)).as("a notification it cannot see")
				.hasMessageContaining("row-level security");
			c.rollback();
		}
		try (Connection c = scoped(companyA, fleetA.id())) {
			markRead(c, fleetA.id(), forFleetA);
			c.rollback();
		}
	}

	@Test
	void aNotificationCarriesIdsNumbersAndCodesOnly() {
		UUID company = seedCompany();
		for (String bad : List.of("{\"reason\":\"Customer called twice\"}", "{\"plate\":\"ABC 123\"}",
				"{\"amountPiastres\":\"1500.00\"}", "{\"amountPiastres\":15.5}", "{\"companyName\":\"Acme\"}",
				"{\"Bad Key\":1}", "[1,2]", "{\"nested\":{\"a\":1}}", "{\"flag\":true}")) {
			assertThatThrownBy(() -> Owner.update(plantSql(), UUID.randomUUID(), "COMPANY", company, "{FLEET_MANAGER}",
					bad, null)).as(bad).hasMessageContaining("check constraint");
		}
		Owner.update(plantSql(), UUID.randomUUID(), "COMPANY", company, "{FLEET_MANAGER}",
				"{\"amountPiastres\":1500,\"companyId\":\"" + company + "\",\"alertType\":\"LOW_BALANCE\"}", null);
		assertThatThrownBy(() -> Owner.update(plantSql(), UUID.randomUUID(), "PLATFORM", null, "{FLEET_MANAGER}", "{}", null))
			.as("a company role in a platform audience").hasMessageContaining("check constraint");
		assertThatThrownBy(() -> Owner.update(plantSql(), UUID.randomUUID(), "COMPANY", null, "{FLEET_MANAGER}", "{}", null))
			.as("a company audience without its company").hasMessageContaining("check constraint");
	}

	// ---------------------------------------------------------------------------------- retention

	@Test
	void notificationsOlderThanTheRetentionArePurgedWithTheirReadState() {
		Scope company = Scope.company(seedCompany());
		SeededUser fleet = seed(Role.FLEET_MANAGER, company, false);
		UUID old = plant(company, "{FLEET_MANAGER}", null);
		UUID recent = plant(company, "{FLEET_MANAGER}", null);
		Owner.update("update notifications set created_at = now() - interval '91 days' where id = ?", old);
		Owner.update("update notifications set created_at = now() - interval '10 days' where id = ?", recent);
		Owner.update("insert into notification_reads (user_id, notification_id, read_at) values (?, ?, now())", fleet.id(),
				old);

		assertThat(retention.purge()).isGreaterThanOrEqualTo(1);
		assertThat(Owner.<Long>queryOne("select count(*) from notifications where id = ?", old)).isZero();
		assertThat(Owner.<Long>queryOne("select count(*) from notification_reads where notification_id = ?", old)).isZero();
		assertThat(Owner.<Long>queryOne("select count(*) from notifications where id = ?", recent)).isEqualTo(1L);
	}

	@Test
	void thePurgeRefusesTheLastThirtyDays() {
		assertThatThrownBy(() -> Owner.queryOne("select purge_notifications(now() - interval '29 days', 10)"))
			.hasMessageContaining("30-day minimum retention");
	}

	// ------------------------------------------------------------------------------------ helpers

	private SignedIn companyUser(UUID company, Role role) throws Exception {
		return signIn(seed(role, Scope.company(company), false));
	}

	private SignedIn stationManager(String operator) throws Exception {
		return signIn(seed(Role.STATION_MANAGER, Scope.operator(UUID.fromString(operator)), false));
	}

	private String openCompanyTicket(SignedIn fleet) throws Exception {
		HttpResponse<String> opened = authed("POST", "/api/v1/company/tickets", fleet.accessToken(),
				"{\"type\":\"LOST_TAG\",\"subject\":\"Tag lost on the road\",\"body\":\"The tag of car 12 fell off.\"}");
		assertThat(opened.statusCode()).as(opened.body()).isEqualTo(201);
		return JsonPath.read(opened.body(), "$.ticket.id");
	}

	private void reply(String ticket, String body) throws Exception {
		HttpResponse<String> answer = authed("POST", "/api/v1/ops/tickets/" + ticket + "/messages", ops.accessToken(), body);
		assertThat(answer.statusCode()).as(answer.body()).isEqualTo(200);
	}

	private String requestTopUp(SignedIn member, long piastres) throws Exception {
		HttpResponse<String> requested = authed("POST", "/api/v1/company/topups", member.accessToken(),
				"{\"amountPiastres\":" + piastres + ",\"bankReference\":\"FT-" + UUID.randomUUID() + "\",\"transferDate\":\""
						+ LocalDate.now(ZoneId.of("Africa/Cairo")) + "\"}");
		assertThat(requested.statusCode()).as(requested.body()).isEqualTo(201);
		return JsonPath.read(requested.body(), "$.id");
	}

	private String operator() throws Exception {
		HttpResponse<String> created = authed("POST", "/api/v1/ops/operators", ops.accessToken(),
				"{\"legalName\":\"Op " + UUID.randomUUID() + "\",\"nameAr\":\"مشغل\",\"nameEn\":\"Operator\"}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		return JsonPath.read(created.body(), "$.id");
	}

	private static void asPlatform(Runnable work) {
		DbScope.runAs(new DbScope.Context(Scope.PLATFORM, null), () -> {
			work.run();
			return null;
		});
	}

	private static long notificationsAbout(UUID entity) {
		return Owner.<Long>queryOne("select count(*) from notifications where entity_id = ?", entity);
	}

	private static String plantSql() {
		return """
				insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
				                           entity_id, params, actor_id, created_at)
				values (?, 'TICKET_ANSWERED', ?, ?, ?::text[], 'TICKET', gen_random_uuid(), ?::jsonb, ?, now())
				""";
	}

	/** A notification written as the owner, bypassing row-level security. */
	private static UUID plant(Scope audience, String roles, UUID actor) {
		UUID id = UUID.randomUUID();
		Owner.update(plantSql(), id, audience.type().name(), audience.id(), roles, "{}", actor);
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

	private List<UUID> visible(Scope scope, UUID userId, List<UUID> among) throws SQLException {
		try (Connection c = scoped(scope, userId);
				PreparedStatement statement = c.prepareStatement("select id from notifications where id = any(?) order by id")) {
			statement.setArray(1, c.createArrayOf("uuid", among.toArray()));
			List<UUID> ids = new java.util.ArrayList<>();
			try (var rs = statement.executeQuery()) {
				while (rs.next()) {
					ids.add(rs.getObject(1, UUID.class));
				}
			}
			c.rollback();
			return ids.stream().sorted(java.util.Comparator.comparing(among::indexOf)).toList();
		}
	}

	private static void insert(Connection c, Scope audience, String actorExpression) throws SQLException {
		try (PreparedStatement statement = c.prepareStatement("""
				insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
				                           entity_id, params, actor_id, created_at)
				values (gen_random_uuid(), 'TICKET_OPENED', ?, ?, ?::text[], 'TICKET', gen_random_uuid(), '{}', """
				+ actorExpression + ", now())")) {
			statement.setString(1, audience.type().name());
			statement.setObject(2, audience.id());
			statement.setString(3, audience.isPlatform() ? "{OPS}" : "{FLEET_MANAGER}");
			statement.executeUpdate();
		}
	}

	private static void markRead(Connection c, UUID userId, UUID notification) throws SQLException {
		try (PreparedStatement statement = c
			.prepareStatement("insert into notification_reads (user_id, notification_id, read_at) values (?, ?, now())")) {
			statement.setObject(1, userId);
			statement.setObject(2, notification);
			statement.executeUpdate();
		}
	}

}
