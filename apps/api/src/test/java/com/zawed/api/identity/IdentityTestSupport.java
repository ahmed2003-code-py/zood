package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.sql.Timestamp;
import java.time.Instant;
import java.util.List;
import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicInteger;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.security.crypto.argon2.Argon2PasswordEncoder;

import com.zawed.api.platform.crypto.DataEncryptor;
import com.zawed.api.platform.ratelimit.InMemoryRateLimiter;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.IntegrationTest;
import com.zawed.api.support.Owner;

/**
 * Seeds users straight into the database (as its owner) and drives the real sign-in flow over HTTP.
 * Every test gets fresh users with unique emails, so tests never depend on each other's data.
 */
public abstract class IdentityTestSupport extends IntegrationTest {

	static final String PASSWORD = "correct horse battery staple";
	static final String WEB_ORIGIN = "http://localhost:5173";

	private static final AtomicInteger SEQUENCE = new AtomicInteger();
	private static final Argon2PasswordEncoder ARGON2 = Argon2PasswordEncoder.defaultsForSpringSecurity_v5_8();

	@Autowired
	DataEncryptor encryptor;

	@Autowired
	InMemoryRateLimiter limiter;

	/** A seeded user and, for MFA roles, the TOTP secret their authenticator would hold. */
	public record SeededUser(UUID id, String email, Role role, Scope scope, byte[] totpSecret) {
	}

	/** An access token and the refresh cookie value from a completed sign-in. */
	public record SignedIn(String accessToken, String refreshToken, UUID userId) {
	}

	@BeforeEach
	void resetRateLimits() {
		limiter.reset();
	}

	public SeededUser seed(Role role) {
		return seed(role, role.isPlatform() ? Scope.PLATFORM : new Scope(role.scopeType(), UUID.randomUUID()), false);
	}

	public SeededUser seed(Role role, Scope scope, boolean mustChangePassword) {
		UUID id = UUID.randomUUID();
		String email = role.name().toLowerCase() + "-" + SEQUENCE.incrementAndGet() + "-" + id.toString().substring(0, 8)
				+ "@zawed.test";
		byte[] secret = role.requiresMfa() ? Totp.newSecret() : null;
		Owner.update("""
				insert into users (id, email, full_name, password_hash, role, scope_type, scope_id, status,
				                   must_change_password, mfa_secret_encrypted, mfa_enabled, created_at, updated_at)
				values (?, ?, ?, ?, ?, ?, ?, 'ACTIVE', ?, ?, ?, now(), now())
				""", id, email, "Test " + role, ARGON2.encode(PASSWORD), role.name(), scope.type().name(), scope.id(),
				mustChangePassword, secret == null ? null : encryptor.encrypt(secret, SignIn.userBinding(id)),
				secret != null);
		return new SeededUser(id, email, role, scope, secret);
	}

	/** A company row with its wallet, written as the owner (row-level security does not apply). */
	public static UUID seedCompany() {
		return seedCompany(0);
	}

	/** A company with its empty wallet and the given low-balance threshold (piastres). */
	public static UUID seedCompany(long lowBalanceThresholdPiastres) {
		UUID id = UUID.randomUUID();
		Owner.update("""
				insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes,
				                       low_balance_threshold_piastres, created_at, updated_at)
				values (?, 'Test Company', 'شركة اختبار', 'Test Company', 'ACTIVE', 180, ?, now(), now())
				""", id, lowBalanceThresholdPiastres);
		// What the ledger module does when tenancy announces a company (V6 backfills existing ones).
		UUID account = UUID.randomUUID();
		Owner.update("""
				insert into ledger_accounts (id, code, kind, company_id, station_id, created_at)
				values (?, ?, 'COMPANY_WALLET', ?, null, now())
				""", account, "company:" + id + ":wallet", id);
		Owner.update("""
				insert into company_wallets (company_id, account_id, balance_piastres, created_at, updated_at)
				values (?, ?, 0, now(), now())
				""", id, account);
		return id;
	}

	/** The current code, or the next step's when {@code ahead} (to avoid the one-use-per-step rule). */
	static String code(byte[] secret, int stepOffset) {
		return Totp.codeAt(secret, Totp.stepAt(Instant.now().getEpochSecond()) + stepOffset);
	}

	/** Signs in through the right door, answering the TOTP step when the role needs one. */
	public SignedIn signIn(SeededUser user) throws Exception {
		String door = user.role() == Role.SUPER_ADMIN ? "/api/v1/admin/auth" : "/api/v1/auth";
		HttpResponse<String> login = post(door + "/login", loginBody(user.email(), PASSWORD));
		HttpResponse<String> last = login;
		if (user.totpSecret() != null) {
			assertThat(JsonPath.<String>read(login.body(), "$.status")).isEqualTo("MFA_REQUIRED");
			String challenge = JsonPath.read(login.body(), "$.challengeToken");
			// Each sign-in of the same user needs a fresh step; walk forward from the current one.
			for (int offset = 0; offset <= 1; offset++) {
				last = post("/api/v1/auth/mfa/verify",
						"{\"challengeToken\":\"" + challenge + "\",\"code\":\"" + code(user.totpSecret(), offset) + "\"}");
				if (last.statusCode() == 200) {
					break;
				}
			}
		}
		assertThat(last.statusCode()).as(last.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(last.body(), "$.status")).isEqualTo("AUTHENTICATED");
		return new SignedIn(JsonPath.read(last.body(), "$.session.accessToken"),
				cookie(last, user.role() == Role.SUPER_ADMIN ? AuthCookies.ADMIN_COOKIE : AuthCookies.USER_COOKIE)
					.orElseThrow(),
				user.id());
	}

	static String loginBody(String email, String password) {
		return "{\"email\":\"" + email + "\",\"password\":\"" + password + "\"}";
	}

	public HttpResponse<String> post(String path, String json) throws Exception {
		return send(request(path).header("Content-Type", "application/json").POST(HttpRequest.BodyPublishers.ofString(json)));
	}

	public HttpResponse<String> authed(String method, String path, String token, String json) throws Exception {
		HttpRequest.Builder builder = request(path).header("Authorization", "Bearer " + token);
		if (json != null) {
			builder.header("Content-Type", "application/json");
		}
		return send(builder.method(method,
				json == null ? HttpRequest.BodyPublishers.noBody() : HttpRequest.BodyPublishers.ofString(json)));
	}

	/** A refresh or logout as the web app sends it: the cookie, our Origin and the client header. */
	HttpResponse<String> withCookie(String path, String cookieName, String value) throws Exception {
		return send(request(path).header("Cookie", cookieName + "=" + value)
			.header("Origin", WEB_ORIGIN)
			.header("X-Zawed-Client", "web")
			.POST(HttpRequest.BodyPublishers.noBody()));
	}

	static Optional<String> cookie(HttpResponse<?> response, String name) {
		return setCookies(response).stream()
			.filter((header) -> header.startsWith(name + "="))
			.map((header) -> header.substring(name.length() + 1, header.indexOf(';')))
			.filter((value) -> !value.isEmpty())
			.findFirst();
	}

	static List<String> setCookies(HttpResponse<?> response) {
		return response.headers().allValues("Set-Cookie");
	}

	public static String code(HttpResponse<String> response) {
		return JsonPath.read(response.body(), "$.code");
	}

	static void backdateRotation(UUID userId, int seconds) {
		Owner.update("update sessions set rotated_at = rotated_at - make_interval(secs => ?) where user_id = ?", seconds,
				userId);
	}

	static Timestamp now() {
		return Timestamp.from(Instant.now());
	}

}
