package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Owner;

/** Contract §3: refresh rotation, race grace, reuse detection, logout, revocation, CSRF rule. */
class SessionTests extends IdentityTestSupport {

	private static final String REFRESH = "/api/v1/auth/refresh";

	@Test
	void refreshRotatesTheCookieAndTheNewAccessTokenWorks() throws Exception {
		SignedIn session = signIn(seed(Role.FLEET_MANAGER));

		HttpResponse<String> refreshed = withCookie(REFRESH, AuthCookies.USER_COOKIE, session.refreshToken());

		assertThat(refreshed.statusCode()).isEqualTo(200);
		String rotated = cookie(refreshed, AuthCookies.USER_COOKIE).orElseThrow();
		assertThat(rotated).isNotEqualTo(session.refreshToken());
		assertThat(authed("GET", "/api/v1/auth/me", JsonPath.read(refreshed.body(), "$.accessToken"), null).statusCode())
			.isEqualTo(200);
	}

	@Test
	void concurrentRefreshesWithTheSameCookieDoNotSignTheUserOut() throws Exception {
		SignedIn session = signIn(seed(Role.ACCOUNTANT));
		ExecutorService pool = Executors.newFixedThreadPool(5);
		try {
			List<Callable<HttpResponse<String>>> calls = new ArrayList<>();
			for (int i = 0; i < 5; i++) {
				calls.add(() -> withCookie(REFRESH, AuthCookies.USER_COOKIE, session.refreshToken()));
			}
			for (Future<HttpResponse<String>> result : pool.invokeAll(calls)) {
				assertThat(result.get().statusCode()).as(result.get().body()).isEqualTo(200);
			}
		}
		finally {
			pool.shutdown();
		}
		assertThat(Owner.queryOne("select revoked_at from sessions where user_id = ?", session.userId())).isNull();
	}

	@Test
	void aRotatedAwayTokenUsedAfterTheGraceWindowEndsTheWholeSession() throws Exception {
		SignedIn session = signIn(seed(Role.STATION_MANAGER));
		HttpResponse<String> first = withCookie(REFRESH, AuthCookies.USER_COOKIE, session.refreshToken());
		String current = cookie(first, AuthCookies.USER_COOKIE).orElseThrow();
		String accessToken = JsonPath.read(first.body(), "$.accessToken");

		// Within the grace window, the old token is a race, not a theft.
		assertThat(withCookie(REFRESH, AuthCookies.USER_COOKIE, session.refreshToken()).statusCode()).isEqualTo(200);

		backdateRotation(session.userId(), 120);
		HttpResponse<String> stolen = withCookie(REFRESH, AuthCookies.USER_COOKIE, session.refreshToken());
		assertThat(stolen.statusCode()).isEqualTo(401);
		assertThat(code(stolen)).isEqualTo("REFRESH_REUSE_DETECTED");

		// The legitimate holder is signed out too: the session itself is gone.
		assertThat(code(withCookie(REFRESH, AuthCookies.USER_COOKIE, current))).isEqualTo("SESSION_EXPIRED");
		assertThat(authed("GET", "/api/v1/auth/me", accessToken, null).statusCode()).isEqualTo(401);
	}

	@Test
	void logoutEndsTheSessionAndItsAccessTokenImmediately() throws Exception {
		SignedIn session = signIn(seed(Role.FLEET_MANAGER));

		HttpResponse<String> logout = withCookie("/api/v1/auth/logout", AuthCookies.USER_COOKIE, session.refreshToken());

		assertThat(logout.statusCode()).isEqualTo(204);
		assertThat(setCookies(logout)).anyMatch((c) -> c.startsWith("zawed_rt=;") && c.contains("Max-Age=0"));
		assertThat(authed("GET", "/api/v1/auth/me", session.accessToken(), null).statusCode()).isEqualTo(401);
		assertThat(code(withCookie(REFRESH, AuthCookies.USER_COOKIE, session.refreshToken()))).isEqualTo("SESSION_EXPIRED");
	}

	@Test
	void suspendingAUserEndsTheirAccessAtTheNextRequest() throws Exception {
		SeededUser user = seed(Role.FLEET_MANAGER);
		SignedIn session = signIn(user);
		Owner.update("update users set status = 'SUSPENDED' where id = ?", user.id());

		HttpResponse<String> me = authed("GET", "/api/v1/auth/me", session.accessToken(), null);
		assertThat(me.statusCode()).isEqualTo(401);
		assertThat(code(me)).isEqualTo("UNAUTHENTICATED");
	}

	@Test
	void refreshAndLogoutRefuseRequestsWithoutOurOriginAndClientHeader() throws Exception {
		SignedIn session = signIn(seed(Role.ACCOUNTANT));

		HttpResponse<String> noHeader = send(request(REFRESH).header("Cookie", "zawed_rt=" + session.refreshToken())
			.header("Origin", WEB_ORIGIN)
			.POST(HttpRequest.BodyPublishers.noBody()));
		HttpResponse<String> foreignOrigin = send(request(REFRESH).header("Cookie", "zawed_rt=" + session.refreshToken())
			.header("Origin", "https://evil.example")
			.header("X-Zawed-Client", "web")
			.POST(HttpRequest.BodyPublishers.noBody()));

		assertThat(code(noHeader)).isEqualTo("CSRF_REJECTED");
		assertThat(foreignOrigin.statusCode()).isEqualTo(403);
	}

	@Test
	void aTemporaryPasswordAllowsOnlyChangingIt() throws Exception {
		SeededUser user = seed(Role.FLEET_MANAGER, Scope.company(java.util.UUID.randomUUID()), true);
		SignedIn first = signIn(user);
		SignedIn second = signIn(user);

		HttpResponse<String> blocked = authed("GET", "/api/v1/auth/sessions", first.accessToken(), null);
		assertThat(blocked.statusCode()).isEqualTo(403);
		assertThat(code(blocked)).isEqualTo("PASSWORD_CHANGE_REQUIRED");
		assertThat(authed("GET", "/api/v1/auth/me", first.accessToken(), null).statusCode()).isEqualTo(200);

		HttpResponse<String> weak = authed("POST", "/api/v1/auth/password", first.accessToken(),
				"{\"currentPassword\":\"" + PASSWORD + "\",\"newPassword\":\"short\"}");
		assertThat(code(weak)).isEqualTo("PASSWORD_TOO_WEAK");

		HttpResponse<String> changed = authed("POST", "/api/v1/auth/password", first.accessToken(),
				"{\"currentPassword\":\"" + PASSWORD + "\",\"newPassword\":\"a much longer chosen password\"}");
		assertThat(changed.statusCode()).isEqualTo(204);

		// This session continues; the other one, which knew the old password, is over.
		assertThat(authed("GET", "/api/v1/auth/sessions", first.accessToken(), null).statusCode()).isEqualTo(200);
		assertThat(authed("GET", "/api/v1/auth/me", second.accessToken(), null).statusCode()).isEqualTo(401);
	}

	@Test
	void theAdminSessionLivesOnItsOwnCookieAndPath() throws Exception {
		SignedIn admin = signIn(seed(Role.SUPER_ADMIN));

		assertThat(code(withCookie(REFRESH, AuthCookies.USER_COOKIE, admin.refreshToken()))).isEqualTo("SESSION_EXPIRED");
		HttpResponse<String> adminRefresh = withCookie("/api/v1/admin/auth/refresh", AuthCookies.ADMIN_COOKIE,
				admin.refreshToken());
		assertThat(adminRefresh.statusCode()).isEqualTo(200);
		assertThat(setCookies(adminRefresh)).anyMatch((c) -> c.startsWith("zawed_admin_rt=") && c.contains("Path=/api/v1/admin/auth"));
	}

}
