package com.zawed.api.notifications;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.net.http.HttpResponse;
import java.sql.Connection;
import java.sql.PreparedStatement;
import java.sql.SQLException;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.UUID;

import javax.sql.DataSource;

import com.jayway.jsonpath.JsonPath;

import io.micrometer.core.instrument.MeterRegistry;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.transaction.support.TransactionTemplate;

import tools.jackson.databind.ObjectMapper;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.notifications.Notifications.Audience;
import com.zawed.api.notifications.Notifications.EntityType;
import com.zawed.api.notifications.Notifications.Notice;
import com.zawed.api.notifications.Notifications.Params;
import com.zawed.api.notifications.Notifications.Type;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Owner;

/**
 * What may be written, by whom, and what a refused notification does to its cause (B7 W5 review):
 * a user-less platform session announces only a low balance or a hard alert (L1); each type carries
 * only its own parameter keys, in Java and in the database alike (L2); and a refused notification
 * never takes a top-up decision or a ticket down with it (M1, the money paths beside the fueling
 * one, which {@code fueling.NotificationFailureTests} covers).
 */
class NotificationWriteRulesTests extends IdentityTestSupport {

	@Autowired
	DataSource dataSource;

	@Autowired
	TransactionTemplate transactions;

	@Autowired
	Notifications notifications;

	@Autowired
	MeterRegistry meters;

	@Autowired
	ObjectMapper json;

	// ------------------------------------------------------------------------------------- L1

	@Test
	void aUserlessPlatformSessionAnnouncesOnlyALowBalanceOrAHardAlert() throws Exception {
		Set<String> written = new TreeSet<>();
		for (Type type : Type.values()) {
			try (Connection c = scoped(Scope.PLATFORM, null)) {
				try {
					insert(c, type, "{}");
					written.add(type.name());
				}
				catch (SQLException ex) {
					assertThat(ex.getMessage()).as(type.name()).contains("row-level security");
				}
				c.rollback();
			}
		}
		assertThat(written).containsExactly("HARD_ALERT_RAISED", "WALLET_LOW_BALANCE");

		// A platform session with a user (Operations at work) may announce any type.
		UUID opsUser = seed(Role.OPS).id();
		for (Type type : Type.values()) {
			try (Connection c = scoped(Scope.PLATFORM, opsUser)) {
				insert(c, type, "{}");
				c.rollback();
			}
		}
	}

	// ------------------------------------------------------------------------------------- L2

	@Test
	void eachTypeCarriesOnlyItsOwnKeysAndTheDatabaseAgreesWithTheCode() {
		UUID company = seedCompany();
		for (Type type : Type.values()) {
			assertThat(Set.of(keysOf(type))).as("database keys of %s", type).isEqualTo(type.keys());

			Map<String, Object> full = new LinkedHashMap<>();
			for (String key : type.keys()) {
				full.put(key, sample(key));
			}
			plant(type, company, json.writeValueAsString(full)); // every key at once is fine
			plant(type, company, "{}"); // and so is none: a key that does not apply is left out

			Map<String, Object> extra = new LinkedHashMap<>(full);
			extra.put("unexpectedKey", 1);
			assertThatThrownBy(() -> plant(type, company, json.writeValueAsString(extra))).as("%s with another key", type)
				.hasMessageContaining("notifications_params");
		}
		// The value rules still hold on an allowed key.
		for (String bad : List.of("{\"amountPiastres\":\"1500.00\"}", "{\"amountPiastres\":15.5}",
				"{\"amountPiastres\":true}", "{\"stationId\":\"Station 4\"}", "{\"stationId\":{\"id\":1}}")) {
			assertThatThrownBy(() -> plant(Type.PAYOUT_POSTED, company, bad)).as(bad)
				.hasMessageContaining("notifications_params");
		}
	}

	@Test
	void aKeyTheTypeDoesNotCarryIsRefusedAtWriteAndCountedAndTheTransactionGoesOn() {
		UUID company = seedCompany();
		UUID bad = UUID.randomUUID();
		UUID good = UUID.randomUUID();
		UUID opsUser = seed(Role.OPS).id();
		double before = dropped(Type.TOPUP_CREDITED);
		Audience audience = Audience.of(Scope.company(company), Role.FLEET_MANAGER);

		DbScope.runAs(new DbScope.Context(Scope.PLATFORM, opsUser), () -> {
			transactions.executeWithoutResult((status) -> {
				notifications.send(Type.TOPUP_CREDITED, () -> new Notice(audience, EntityType.TOPUP, bad,
						Params.of().number("amountPiastres", 100).code("ticketType", "OTHER")));
				// Still in a healthy transaction: an aborted one would refuse this statement.
				notifications.send(Type.TOPUP_CREDITED, () -> new Notice(audience, EntityType.TOPUP, good,
						Params.of().number("amountPiastres", 100)));
			});
			return null;
		});

		assertThat(Owner.<Long>queryOne("select count(*) from notifications where entity_id = ?", bad)).isZero();
		assertThat(Owner.<Long>queryOne("select count(*) from notifications where entity_id = ?", good)).isEqualTo(1L);
		assertThat(dropped(Type.TOPUP_CREDITED) - before).isEqualTo(1);
	}

	// ------------------------------------------------------------- M1 beyond the fueling path

	@Test
	void aTopUpIsVerifiedAndCreditedAndATicketOpenedWhenTheirNotificationsAreRefused() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		SignedIn verifier = signIn(seed(Role.OPS_MANAGER));
		SignedIn approver = signIn(seed(Role.OPS_MANAGER));
		String topUp = requestTopUp(fleet, 250_000);
		double before = dropped(Type.TOPUP_AWAITING_APPROVAL) + dropped(Type.TOPUP_CREDITED) + dropped(Type.TICKET_OPENED);

		String trigger = refuseNotificationsAbout(company);
		try {
			HttpResponse<String> verified = authed("POST", "/api/v1/ops/topups/" + topUp + "/verify",
					verifier.accessToken(), "{\"receivedAmountPiastres\":240000}");
			assertThat(verified.statusCode()).as(verified.body()).isEqualTo(200);
			HttpResponse<String> approved = authed("POST", "/api/v1/ops/topups/" + topUp + "/approve",
					approver.accessToken(), null);
			assertThat(approved.statusCode()).as(approved.body()).isEqualTo(200);
			HttpResponse<String> ticket = authed("POST", "/api/v1/company/tickets", fleet.accessToken(),
					"{\"type\":\"LOST_TAG\",\"subject\":\"Tag lost\",\"body\":\"The tag of car 12 fell off.\"}");
			assertThat(ticket.statusCode()).as(ticket.body()).isEqualTo(201);
			String ticketId = JsonPath.read(ticket.body(), "$.ticket.id");

			assertThat(Owner.<Long>queryOne("select balance_piastres from company_wallets where company_id = ?", company))
				.as("the money moved").isEqualTo(240_000L);
			assertThat(Owner.<String>queryOne("select status from topup_requests where id = ?::uuid", topUp)).isEqualTo("CONFIRMED");
			assertThat(Owner.<Long>queryOne("select count(*) from tickets where id = ?::uuid", ticketId)).isEqualTo(1L);
			assertThat(Owner.<Long>queryOne("""
					select count(*) from notifications where audience_scope_id = ? or params ->> 'companyId' = ?
					""", company, company.toString())).as("and nothing about it was written").isZero();
		}
		finally {
			Owner.update("drop trigger " + trigger + " on notifications");
			Owner.update("drop function " + trigger + "()");
		}
		double after = dropped(Type.TOPUP_AWAITING_APPROVAL) + dropped(Type.TOPUP_CREDITED) + dropped(Type.TICKET_OPENED);
		assertThat(after - before).isEqualTo(3);
	}

	// ---------------------------------------------------------------------------------- helpers

	private double dropped(Type type) {
		return meters.get(Notifications.DROPPED_METRIC).tag("type", type.name()).counter().count();
	}

	private static String[] keysOf(Type type) {
		try (Connection c = Owner.connect();
				PreparedStatement statement = c.prepareStatement("select notification_param_keys(?)")) {
			statement.setString(1, type.name());
			try (var rs = statement.executeQuery()) {
				rs.next();
				return (String[]) rs.getArray(1).getArray();
			}
		}
		catch (SQLException ex) {
			throw new IllegalStateException(ex);
		}
	}

	private static Object sample(String key) {
		if (key.endsWith("Id")) {
			return UUID.randomUUID().toString();
		}
		if (key.endsWith("Piastres")) {
			return 1_500;
		}
		return "LOW_BALANCE";
	}

	/** A notification written as the owner, so only the table's own checks apply. */
	private static void plant(Type type, UUID company, String params) {
		Owner.update("""
				insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
				                           entity_id, params, actor_id, created_at)
				values (gen_random_uuid(), ?, 'COMPANY', ?, '{FLEET_MANAGER}', 'TOPUP', gen_random_uuid(), ?::jsonb, null,
				        now())
				""", type.name(), company, params);
	}

	private static void insert(Connection c, Type type, String params) throws SQLException {
		try (PreparedStatement statement = c.prepareStatement("""
				insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
				                           entity_id, params, actor_id, created_at)
				values (gen_random_uuid(), ?, 'PLATFORM', null, '{OPS}', 'ALERT', gen_random_uuid(), ?::jsonb,
				        app_user_id(), now())
				""")) {
			statement.setString(1, type.name());
			statement.setString(2, params);
			statement.executeUpdate();
		}
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

	/** A trigger that refuses every notification addressed to this company or about it, until dropped. */
	private static String refuseNotificationsAbout(UUID company) {
		String name = "test_refuse_" + UUID.randomUUID().toString().replace("-", "");
		Owner.update("create function " + name + "() returns trigger language plpgsql as $$ begin "
				+ "raise exception 'notification refused by the test'; end $$");
		Owner.update("create trigger " + name + " before insert on notifications for each row when ("
				+ "new.audience_scope_id = '" + company + "' or new.params ->> 'companyId' = '" + company + "') "
				+ "execute function " + name + "()");
		return name;
	}

	private String requestTopUp(SignedIn member, long piastres) throws Exception {
		HttpResponse<String> requested = authed("POST", "/api/v1/company/topups", member.accessToken(),
				"{\"amountPiastres\":" + piastres + ",\"bankReference\":\"FT-" + UUID.randomUUID() + "\",\"transferDate\":\""
						+ LocalDate.now(ZoneId.of("Africa/Cairo")) + "\"}");
		assertThat(requested.statusCode()).as(requested.body()).isEqualTo(201);
		return JsonPath.read(requested.body(), "$.id");
	}

}
