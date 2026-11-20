package com.zawed.api.platform.security;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.springframework.mock.web.MockFilterChain;
import org.springframework.mock.web.MockHttpServletRequest;
import org.springframework.mock.web.MockHttpServletResponse;
import org.springframework.security.core.context.SecurityContextHolder;
import org.springframework.web.servlet.ModelAndView;

import com.zawed.api.platform.web.ApiException;

/**
 * The second layer of B7 "path matching": even for a request that reached them with a
 * non-canonical path (were {@code CanonicalPathFilter} ever removed or bypassed), our security
 * filters see the path as Spring Security and Spring MVC route it, not as a raw string.
 */
class SecurityFiltersPathMatchingTests {

	private final List<Exception> resolved = new ArrayList<>();

	private final SecurityFilters filters = new SecurityFilters((request, response, handler, ex) -> {
		resolved.add(ex);
		return new ModelAndView();
	});

	@AfterEach
	void clearContext() {
		SecurityContextHolder.clearContext();
	}

	@Test
	void theSignInLimitCountsAnEncodedSpellingOfTheSignInPath() throws Exception {
		for (String path : List.of("/api/v1/auth/login", "/api/v1/auth/logi%6E", "/api/v1/%61uth/login",
				"/api/v1/auth/mfa/verif%79", "/api/v1/setup/super-admi%6E")) {
			List<String> keys = new ArrayList<>();
			filters.rateLimits((key, limit, window) -> keys.add(key + "/" + limit))
				.doFilter(request("POST", path), new MockHttpServletResponse(), new MockFilterChain());
			assertThat(keys).as(path).singleElement().asString().startsWith("signin:").endsWith("/10");
		}
	}

	@Test
	void theRefreshLimitCountsAnEncodedSpellingOfTheRefreshPath() throws Exception {
		List<String> keys = new ArrayList<>();
		filters.rateLimits((key, limit, window) -> keys.add(key))
			.doFilter(request("POST", "/api/v1/auth/refres%68"), new MockHttpServletResponse(), new MockFilterChain());
		assertThat(keys).singleElement().asString().startsWith("refresh-ip:");
	}

	@Test
	void thePasswordChangeGateReadsThePathAsRouted() throws Exception {
		SecurityContextHolder.getContext()
			.setAuthentication(new ZawedPrincipal(UUID.randomUUID(), UUID.randomUUID(), Role.FLEET_MANAGER,
					new Scope(ScopeType.COMPANY, UUID.randomUUID()), true)
				.toAuthentication());

		MockFilterChain allowed = new MockFilterChain();
		filters.passwordChangeGate()
			.doFilter(request("GET", "/api/v1/auth/m%65"), new MockHttpServletResponse(), allowed);
		assertThat(allowed.getRequest()).as("me, spelled another way, is still me").isNotNull();
		assertThat(resolved).isEmpty();

		MockFilterChain refused = new MockFilterChain();
		filters.passwordChangeGate()
			.doFilter(request("POST", "/api/v1/auth/m%65"), new MockHttpServletResponse(), refused);
		assertThat(refused.getRequest()).as("the method still counts").isNull();
		assertThat(resolved).singleElement()
			.isInstanceOfSatisfying(ApiException.class, (ex) -> assertThat(ex.code()).isEqualTo("PASSWORD_CHANGE_REQUIRED"));
	}

	@Test
	void thePosPublicPathsAreMatchedAsRoutedAndOnlyForPost() throws Exception {
		List<String> authenticated = new ArrayList<>();
		PosAuthenticator authenticator = (request) -> {
			authenticated.add(request.getRequestURI());
			throw new ApiException(org.springframework.http.HttpStatus.UNAUTHORIZED, "DEVICE_UNAUTHENTICATED", "no");
		};
		var publicPaths = SecurityFilters.post("/api/v1/pos/activate", "/api/v1/pos/token");

		for (String path : List.of("/api/v1/pos/activate", "/api/v1/pos/activat%65", "/api/v1/pos/toke%6E")) {
			MockFilterChain chain = new MockFilterChain();
			filters.posAuthentication(authenticator, publicPaths)
				.doFilter(request("POST", path), new MockHttpServletResponse(), chain);
			assertThat(chain.getRequest()).as(path).isNotNull();
		}
		assertThat(authenticated).isEmpty();

		filters.posAuthentication(authenticator, publicPaths)
			.doFilter(request("GET", "/api/v1/pos/activate"), new MockHttpServletResponse(), new MockFilterChain());
		filters.posAuthentication(authenticator, publicPaths)
			.doFilter(request("POST", "/api/v1/po%73/sync"), new MockHttpServletResponse(), new MockFilterChain());
		assertThat(authenticated).containsExactly("/api/v1/pos/activate", "/api/v1/po%73/sync");
	}

	private static MockHttpServletRequest request(String method, String rawPath) {
		MockHttpServletRequest request = new MockHttpServletRequest(method, rawPath);
		request.setRemoteAddr("192.0.2.10");
		return request;
	}

}
