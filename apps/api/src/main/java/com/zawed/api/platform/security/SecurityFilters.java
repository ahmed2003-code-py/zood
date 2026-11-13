package com.zawed.api.platform.security;

import java.io.IOException;
import java.time.Duration;
import java.util.Set;

import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;

import org.springframework.beans.factory.annotation.Qualifier;
import org.springframework.http.HttpStatus;
import org.springframework.security.core.Authentication;
import org.springframework.security.core.context.SecurityContextHolder;
import org.springframework.stereotype.Component;
import org.springframework.web.filter.OncePerRequestFilter;
import org.springframework.web.servlet.HandlerExceptionResolver;

import com.zawed.api.platform.crypto.SecureTokens;
import com.zawed.api.platform.ratelimit.RateLimiter;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.ClientInfo;

/**
 * Filters that run after the access token is checked. They are created here rather than as beans,
 * so the servlet container does not also register them outside the security chain.
 */
@Component
public class SecurityFilters {

	/** What a user who must change their password can still do (contract §4). */
	private static final Set<String> ALLOWED_BEFORE_PASSWORD_CHANGE = Set.of("GET /api/v1/auth/me",
			"POST /api/v1/auth/password", "POST /api/v1/auth/logout", "POST /api/v1/admin/auth/logout");

	/** Anonymous sign-in steps: 10 per minute per address (contract §6). */
	private static final Set<String> SIGN_IN_PATHS = Set.of("/api/v1/auth/login", "/api/v1/admin/auth/login",
			"/api/v1/auth/mfa/verify", "/api/v1/auth/mfa/enroll/start", "/api/v1/auth/mfa/enroll/confirm",
			"/api/v1/setup/super-admin");

	private static final Set<String> REFRESH_PATHS = Set.of("/api/v1/auth/refresh", "/api/v1/admin/auth/refresh");

	static final int SIGN_IN_PER_MINUTE = 10;
	static final int REFRESH_PER_MINUTE = 30;
	static final int AUTHENTICATED_PER_MINUTE = 300;

	private final HandlerExceptionResolver resolver;

	SecurityFilters(@Qualifier("handlerExceptionResolver") HandlerExceptionResolver resolver) {
		this.resolver = resolver;
	}

	OncePerRequestFilter passwordChangeGate() {
		return new OncePerRequestFilter() {
			@Override
			protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain chain)
					throws ServletException, IOException {
				ZawedPrincipal principal = principal();
				if (principal != null && principal.mustChangePassword()
						&& !ALLOWED_BEFORE_PASSWORD_CHANGE.contains(request.getMethod() + " " + request.getRequestURI())) {
					resolver.resolveException(request, response, null, new ApiException(HttpStatus.FORBIDDEN,
							"PASSWORD_CHANGE_REQUIRED", "Change the temporary password first"));
					return;
				}
				chain.doFilter(request, response);
			}
		};
	}

	OncePerRequestFilter rateLimits(RateLimiter limiter) {
		return new OncePerRequestFilter() {
			@Override
			protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain chain)
					throws ServletException, IOException {
				try {
					String path = request.getRequestURI();
					ZawedPrincipal principal = principal();
					if (principal != null) {
						limiter.acquire("user:" + principal.userId(), AUTHENTICATED_PER_MINUTE, Duration.ofMinutes(1));
					}
					else if (SIGN_IN_PATHS.contains(path)) {
						limiter.acquire("signin:" + addressKey(request), SIGN_IN_PER_MINUTE, Duration.ofMinutes(1));
					}
					else if (REFRESH_PATHS.contains(path)) {
						limiter.acquire("refresh:" + addressKey(request), REFRESH_PER_MINUTE, Duration.ofMinutes(1));
					}
				}
				catch (ApiException ex) {
					resolver.resolveException(request, response, null, ex);
					return;
				}
				chain.doFilter(request, response);
			}
		};
	}

	/** The address is hashed: rate-limit keys sit in memory and must not become a log of client IPs. */
	private static String addressKey(HttpServletRequest request) {
		return SecureTokens.sha256Hex(String.valueOf(ClientInfo.address(request)));
	}

	private static ZawedPrincipal principal() {
		Authentication authentication = SecurityContextHolder.getContext().getAuthentication();
		return authentication != null && authentication.getPrincipal() instanceof ZawedPrincipal principal ? principal
				: null;
	}

}
