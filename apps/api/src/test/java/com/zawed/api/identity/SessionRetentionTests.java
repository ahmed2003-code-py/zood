package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.net.http.HttpResponse;
import java.sql.Connection;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;

import com.zawed.api.platform.security.Role;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * Web sessions and MFA challenges are purged after their retention (V21, ADR 0028), through bounded
 * SECURITY DEFINER functions with their own floors, and never touch a live session, nor the station
 * agents' sessions that shifts are built from.
 */
class SessionRetentionTests extends IdentityTestSupport {

	private static final String REFRESH = "/api/v1/auth/refresh";

	@Autowired
	SessionRetention retention;

	// ------------------------------------------------------------------------------------ sessions

	@Test
	void onlySessionsThatEndedBeforeTheCutoffArePurgedAndALiveOneNeverIsHoweverOld() throws SQLException {
		UUID user = seed(Role.FLEET_MANAGER).id();
		String justOver = "now() - interval '31 days 1 minute'";
		String justUnder = "now() - interval '30 days 23 hours 59 minutes'";
		String future = "now() + interval '1 hour'";
		// Live: signed in 400 days ago (impossible with a 30-day absolute end, so the strongest case).
		UUID live = plantSession(user, "now() - interval '400 days'", future, future, null);
		UUID revokedOver = plantSession(user, "now() - interval '40 days'", future, future, justOver);
		UUID revokedUnder = plantSession(user, "now() - interval '40 days'", future, future, justUnder);
		UUID idleOver = plantSession(user, "now() - interval '40 days'", justOver, future, null);
		UUID idleUnder = plantSession(user, "now() - interval '40 days'", justUnder, future, null);
		UUID absoluteOver = plantSession(user, "now() - interval '60 days'", future, justOver, null);
		// Idle-expired long ago, then revoked yesterday (a password change revokes expired rows too):
		// it ended at the earlier of the two.
		UUID expiredThenRevoked = plantSession(user, "now() - interval '120 days'", "now() - interval '100 days'",
				"now() - interval '90 days'", "now() - interval '1 day'");
		List<UUID> usableBefore = usableSessions();

		// Scheduling is on in the shared context, so the real job may delete some of these first:
		// the assertions are on which rows remain, never on how many this call deleted.
		try (Connection connection = app(null); Statement sql = connection.createStatement()) {
			sql.executeQuery("select purge_sessions(now() - interval '31 days', 50000)").close();
			connection.commit();
		}

		assertThat(sessionsOf(user)).containsExactlyInAnyOrder(live, revokedUnder, idleUnder);
		assertThat(sessionsOf(user)).doesNotContain(revokedOver, idleOver, absoluteOver, expiredThenRevoked);
		// The property itself, over the whole table: nothing that Sessions.usableAt accepts was deleted.
		assertThat(existing(usableBefore)).as("usable sessions still present").containsExactlyInAnyOrderElementsOf(usableBefore);
	}

	@Test
	void theJobUsesTheNinetyDayDefaultAndNeverAsksForLessThanTheFloorPlusADay() {
		UUID user = seed(Role.ACCOUNTANT).id();
		UUID old = plantSession(user, "now() - interval '120 days'", "now() - interval '91 days'",
				"now() - interval '90 days'", null);
		UUID kept = plantSession(user, "now() - interval '120 days'", "now() - interval '89 days'",
				"now() - interval '89 days'", "now() - interval '89 days'");

		retention.purgeSessions();
		assertThat(sessionsOf(user)).containsExactly(kept).doesNotContain(old);

		assertThat(SessionRetention.SESSION_FLOOR).isEqualTo(Duration.ofDays(30));
		assertThat(SessionRetention.atLeast(Duration.ofDays(1), SessionRetention.SESSION_MINIMUM))
			.isEqualTo(Duration.ofDays(31));
		assertThat(SessionRetention.atLeast(Duration.ofDays(90), SessionRetention.SESSION_MINIMUM))
			.isEqualTo(Duration.ofDays(90));
		assertThat(SessionRetention.atLeast(Duration.ofMinutes(5), SessionRetention.CHALLENGE_MINIMUM))
			.isEqualTo(Duration.ofHours(2));
	}

	@Test
	void theFunctionsRefuseACutoffInsideTheirFloorWhoeverCallsThem() throws SQLException {
		String[][] refused = { { "purge_sessions", "now() - interval '29 days'", "30-day minimum" },
				{ "purge_sessions", "now() - interval '29 days 23 hours'", "30-day minimum" },
				{ "purge_sessions", "null", "30-day minimum" },
				{ "purge_sessions", "now() + interval '1 day'", "30-day minimum" },
				{ "purge_mfa_challenges", "now() - interval '59 minutes'", "1-hour minimum" },
				{ "purge_mfa_challenges", "null", "1-hour minimum" } };
		for (String[] call : refused) {
			for (String scope : new String[] { "PLATFORM", "COMPANY", null }) {
				try (Connection connection = app(scope); Statement sql = connection.createStatement()) {
					assertThatThrownBy(() -> sql.executeQuery("select " + call[0] + "(" + call[1] + ", 100)"))
						.as("%s(%s) as %s", call[0], call[1], scope)
						.hasMessageContaining(call[2]);
				}
			}
		}
	}

	@Test
	void theRuntimeRoleCannotDeleteSessionsChallengesOrAgentSessionsDirectly() throws SQLException {
		for (String table : new String[] { "sessions", "mfa_challenges", "agent_sessions", "job_runs" }) {
			assertThat(Owner.<Boolean>queryOne("select has_table_privilege('zawed_app', cast(? as text), 'DELETE')", table))
				.as("DELETE on %s", table)
				.isFalse();
			for (String scope : new String[] { "PLATFORM", "COMPANY", null }) {
				try (Connection connection = app(scope); Statement sql = connection.createStatement()) {
					assertThatThrownBy(() -> sql.executeUpdate("delete from " + table + " where false"))
						.as("delete from %s as %s", table, scope)
						.hasMessageContaining("permission denied");
				}
			}
		}
	}

	// ---------------------------------------------------------------------------- agent sessions

	@Test
	void agentSessionsAreNeverPurgedWhateverTheirAge() throws SQLException {
		// Planted with foreign keys switched off for this connection only, and removed afterwards: the
		// rows only need to look like shifts that ended over a year ago.
		List<UUID> planted = new ArrayList<>();
		try {
			try (Connection owner = Owner.connect(); Statement sql = owner.createStatement()) {
				sql.execute("set session_replication_role = replica");
				for (String revoked : new String[] { "now() - interval '400 days'", "null" }) {
					UUID id = UUID.randomUUID();
					planted.add(id);
					sql.execute("""
							insert into agent_sessions (id, agent_id, device_id, operator_id, station_id, token_hash,
							                            created_at, last_seen_at, expires_at, revoked_at, revoke_reason)
							values ('%s', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(),
							        'agent-retention-%s', now() - interval '401 days', now() - interval '401 days',
							        now() - interval '400 days', %s, %s)
							""".formatted(id, id, revoked, "null".equals(revoked) ? "null" : "'SIGNED_OUT'"));
				}
			}
			try (Connection connection = app(null); Statement sql = connection.createStatement()) {
				sql.executeQuery("select purge_sessions(now() - interval '31 days', 50000)").close();
				sql.executeQuery("select purge_mfa_challenges(now() - interval '2 hours', 50000)").close();
				connection.commit();
			}
			retention.scheduled();

			assertThat(Owner.<Long>queryOne("select count(*) from agent_sessions where id = any(cast(? as uuid[]))",
					uuidArray(planted))).isEqualTo(2L);
			// And by construction: neither function names the table.
			for (String function : new String[] { "purge_sessions(timestamptz, integer)",
					"purge_mfa_challenges(timestamptz, integer)" }) {
				String body = Owner.queryOne("select pg_get_functiondef(cast(? as regprocedure))", function);
				assertThat(body).as(function).doesNotContainIgnoringCase("agent_sessions");
			}
			assertThat(Owner.<String>queryOne("select pg_get_functiondef(cast(? as regprocedure))",
					"purge_sessions(timestamptz, integer)")).contains("DELETE FROM sessions s");
		}
		finally {
			for (UUID id : planted) {
				Owner.update("delete from agent_sessions where id = ?", id);
			}
		}
	}

	// ---------------------------------------------------------------------------------- challenges

	@Test
	void expiredChallengesArePurgedAndLiveOnesKept() throws SQLException {
		UUID user = seed(Role.OPS).id();
		UUID old = plantChallenge(user, "now() - interval '2 days'");
		UUID recent = plantChallenge(user, "now() - interval '2 hours'");
		UUID live = plantChallenge(user, "now() + interval '5 minutes'");
		UUID justExpired = plantChallenge(user, "now() - interval '1 second'");

		retention.purgeChallenges();
		assertThat(challengesOf(user)).containsExactlyInAnyOrder(recent, live, justExpired).doesNotContain(old);

		try (Connection connection = app(null); Statement sql = connection.createStatement()) {
			sql.executeQuery("select purge_mfa_challenges(now() - interval '1 hour', 50000)").close();
			connection.commit();
		}
		assertThat(challengesOf(user)).containsExactlyInAnyOrder(live, justExpired);
	}

	@Test
	void anEnrolmentUnderWaySurvivesTheStrictestPurgeAndCompletes() throws Exception {
		// A user who must enrol a second factor: the ENROLL challenge holds the pending secret.
		SeededUser ops = seed(Role.OPS);
		Owner.update("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = ?", ops.id());
		HttpResponse<String> login = post("/api/v1/auth/login", loginBody(ops.email(), PASSWORD));
		assertThat(JsonPath.<String>read(login.body(), "$.status")).isEqualTo("MFA_ENROLLMENT_REQUIRED");
		String challenge = JsonPath.read(login.body(), "$.challengeToken");
		HttpResponse<String> start = post("/api/v1/auth/mfa/enroll/start", "{\"challengeToken\":\"" + challenge + "\"}");
		assertThat(start.statusCode()).as(start.body()).isEqualTo(200);
		assertThat(Owner.<Long>queryOne("""
				select count(*) from mfa_challenges
				where user_id = ? and purpose = 'ENROLL' and pending_secret_encrypted is not null and used_at is null
				""", ops.id())).isEqualTo(1L);

		// The strictest cutoff the database accepts, and the job itself.
		try (Connection connection = app(null); Statement sql = connection.createStatement()) {
			sql.executeQuery("select purge_mfa_challenges(now() - interval '1 hour', 50000)").close();
			connection.commit();
		}
		retention.scheduled();

		HttpResponse<String> confirm = post("/api/v1/auth/mfa/enroll/confirm", "{\"challengeToken\":\"" + challenge
				+ "\",\"code\":\"" + code(base32Decode(JsonPath.read(start.body(), "$.secret")), 0) + "\"}");
		assertThat(confirm.statusCode()).as(confirm.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(confirm.body(), "$.status")).isEqualTo("AUTHENTICATED");
	}

	// --------------------------------------------------------------------------------- concurrency

	@Test
	void refreshesAndSignInsRacingThePurgeLoseNoLiveSession() throws Exception {
		int users = 4;
		List<SeededUser> seeded = new ArrayList<>();
		List<SignedIn> sessions = new ArrayList<>();
		for (int i = 0; i < users; i++) {
			SeededUser user = seed(i % 2 == 0 ? Role.FLEET_MANAGER : Role.ACCOUNTANT);
			seeded.add(user);
			sessions.add(signIn(user));
		}
		// Plenty of dead rows next to the live ones, on the same users and the same indexes.
		for (SeededUser user : seeded) {
			Owner.update("""
					insert into sessions (id, user_id, door, refresh_token_hash, created_at, last_seen_at, expires_at,
					                      absolute_expires_at, revoked_at, revoked_reason)
					select gen_random_uuid(), ?, 'USER', 'dead-' || gen_random_uuid(), now() - interval '150 days',
					       now() - interval '150 days', now() - interval '140 days', now() - interval '120 days',
					       case when g % 2 = 0 then now() - interval '145 days' end,
					       case when g % 2 = 0 then 'LOGOUT' end
					from generate_series(1, 600) g
					""", user.id());
		}
		List<UUID> usableBefore = usableSessions();

		List<String> outcomes = Concurrently.run(users + 4, (index) -> {
			if (index < users) {
				// A user refreshing again and again, each time with the cookie the last refresh set.
				String cookie = sessions.get(index).refreshToken();
				String accessToken = null;
				for (int round = 0; round < 8; round++) {
					HttpResponse<String> refreshed = withCookie(REFRESH, AuthCookies.USER_COOKIE, cookie);
					if (refreshed.statusCode() != 200) {
						return "refresh " + refreshed.statusCode() + " " + refreshed.body();
					}
					cookie = cookie(refreshed, AuthCookies.USER_COOKIE).orElse(cookie);
					accessToken = JsonPath.read(refreshed.body(), "$.accessToken");
				}
				int me = authed("GET", "/api/v1/auth/me", accessToken, null).statusCode();
				return me == 200 ? "ok" : "me " + me;
			}
			if (index < users + 2) {
				// Two purgers in small batches, as two instances would run it.
				int deleted;
				do {
					try (Connection connection = app(null); Statement sql = connection.createStatement();
							ResultSet rs = sql.executeQuery("select purge_sessions(now() - interval '31 days', 50)")) {
						rs.next();
						deleted = rs.getInt(1);
						connection.commit();
					}
				}
				while (deleted > 0);
				return "ok";
			}
			if (index == users + 2) {
				retention.scheduled();
				return "ok";
			}
			// New sign-ins while the purge runs.
			for (int i = 0; i < 3; i++) {
				signIn(seed(Role.FLEET_MANAGER));
			}
			return "ok";
		});

		assertThat(outcomes).containsOnly("ok");
		for (SeededUser user : seeded) {
			assertThat(Owner.<Long>queryOne("select count(*) from sessions where user_id = ? and refresh_token_hash like 'dead-%'",
					user.id())).as("dead sessions left").isZero();
			assertThat(Owner.<Long>queryOne("""
					select count(*) from sessions
					where user_id = ? and revoked_at is null and expires_at > now() and absolute_expires_at > now()
					""", user.id())).as("the user's live session").isEqualTo(1L);
		}
		assertThat(existing(usableBefore)).containsExactlyInAnyOrderElementsOf(usableBefore);
	}

	// ------------------------------------------------------------------------------------ the job

	@Test
	void eachRunIsRecordedWithItsCounts() {
		retention.scheduled();
		assertThat(Owner.<String>queryOne("""
				select outcome || ' ' || (counts ->> 'sessions' ~ '^[0-9]+$') || ' ' || (counts ->> 'mfaChallenges' ~ '^[0-9]+$')
				from job_runs where job_name = 'session-retention' order by started_at desc, id desc limit 1
				""")).isEqualTo("SUCCESS true true");
	}

	// ----------------------------------------------------------------------------------- helpers

	/** Each argument is a SQL expression; {@code revokedAt} may be null. */
	private static UUID plantSession(UUID user, String createdAt, String expiresAt, String absoluteExpiresAt,
			String revokedAt) {
		UUID id = UUID.randomUUID();
		Owner.update("""
				insert into sessions (id, user_id, door, refresh_token_hash, created_at, last_seen_at, expires_at,
				                      absolute_expires_at, revoked_at, revoked_reason)
				values (?, ?, 'USER', ?, %1$s, %1$s, %2$s, %3$s, %4$s, %5$s)
				""".formatted(createdAt, expiresAt, absoluteExpiresAt, revokedAt == null ? "null" : revokedAt,
				revokedAt == null ? "null" : "'LOGOUT'"), id, user, "planted-" + id);
		return id;
	}

	private static UUID plantChallenge(UUID user, String expiresAt) {
		UUID id = UUID.randomUUID();
		Owner.update("""
				insert into mfa_challenges (id, user_id, purpose, door, token_hash, scope_type, scope_id, created_at,
				                            expires_at)
				values (?, ?, 'VERIFY', 'USER', ?, 'PLATFORM', null, (%1$s) - interval '5 minutes', %1$s)
				""".formatted(expiresAt), id, user, "planted-" + id);
		return id;
	}

	private static List<UUID> sessionsOf(UUID user) {
		return Owner.queryList("select id from sessions where user_id = ? and refresh_token_hash like 'planted-%'", user);
	}

	private static List<UUID> challengesOf(UUID user) {
		return Owner.queryList("select id from mfa_challenges where user_id = ?", user);
	}

	/** Every session that {@code Sessions.usableAt} would accept now. */
	private static List<UUID> usableSessions() {
		return Owner.queryList("""
				select id from sessions
				where revoked_at is null and expires_at > now() + interval '5 minutes'
				  and absolute_expires_at > now() + interval '5 minutes'
				""");
	}

	private static List<UUID> existing(List<UUID> ids) {
		return Owner.queryList("select id from sessions where id = any(cast(? as uuid[]))", uuidArray(ids));
	}

	private static byte[] base32Decode(String value) {
		String alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
		byte[] out = new byte[value.length() * 5 / 8];
		int buffer = 0;
		int bits = 0;
		int index = 0;
		for (char c : value.toCharArray()) {
			buffer = (buffer << 5) | alphabet.indexOf(c);
			bits += 5;
			if (bits >= 8) {
				out[index++] = (byte) (buffer >> (bits - 8));
				bits -= 8;
			}
		}
		return out;
	}

	/** A PostgreSQL array literal of ids, for {@code cast(? as uuid[])}. */
	private static String uuidArray(List<UUID> ids) {
		return "{" + String.join(",", ids.stream().map(UUID::toString).toList()) + "}";
	}

	/** A transaction as the runtime role, told what {@code ScopedTransactionManager} would tell it. */
	private static Connection app(String scopeType) throws SQLException {
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
		}
		return connection;
	}

}
