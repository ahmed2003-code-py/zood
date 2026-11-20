package com.zawed.api.platform.web;

import java.io.IOException;
import java.util.regex.Pattern;

import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;

import org.springframework.beans.factory.annotation.Qualifier;
import org.springframework.core.Ordered;
import org.springframework.core.annotation.Order;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Component;
import org.springframework.web.filter.OncePerRequestFilter;
import org.springframework.web.servlet.HandlerExceptionResolver;

/**
 * Refuses every request whose path is not already in its one canonical spelling (B7 "path
 * matching").
 *
 * <p>Spring Security and Spring MVC match a path by its decoded segments with matrix parameters
 * removed, so {@code /api/v1/auth/logi%6E} is the sign-in endpoint to them. Anything that looks at
 * the raw path (a rate limit by path, an allow-list) would see a different, unknown path, and the
 * firewall lets percent-encoded unreserved characters through. Refusing every spelling but the
 * canonical one makes the raw path, the decoded path and the routed path the same string for every
 * request that gets further, whatever reads it.
 *
 * <p>Canonical means: segments of unreserved characters only ({@code A-Z a-z 0-9 - . _ ~}, RFC 3986
 * §2.3), no empty, {@code .} or {@code ..} segment, an optional trailing slash. No route of this API
 * needs anything else: every path variable is a UUID. The query string is not part of the path and
 * keeps its ordinary encoding.
 *
 * <p>It runs right after {@link RequestIdFilter}, so the refusal carries the request id, and before
 * the security filters, so nothing downstream ever sees a non-canonical path.
 */
@Component
@Order(Ordered.HIGHEST_PRECEDENCE + 1)
public class CanonicalPathFilter extends OncePerRequestFilter {

	public static final String CODE = "PATH_NOT_CANONICAL";

	// Possessive quantifiers: a greedy repeated group recurses once per segment in java.util.regex, so a
	// long path of short segments would overflow the stack (a 500 and an ERROR line on demand).
	private static final Pattern CANONICAL = Pattern.compile("(?:/(?!\\.\\.?(?:/|$))[A-Za-z0-9._~-]++)*+/?");

	private final HandlerExceptionResolver resolver;

	CanonicalPathFilter(@Qualifier("handlerExceptionResolver") HandlerExceptionResolver resolver) {
		this.resolver = resolver;
	}

	/** Whether the raw request path (as sent, before any decoding) is canonical. */
	static boolean isCanonical(String rawPath) {
		return rawPath != null && !rawPath.isEmpty() && CANONICAL.matcher(rawPath).matches();
	}

	@Override
	protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain chain)
			throws ServletException, IOException {
		// getRequestURI is the raw path: not decoded, not normalized, matrix parameters kept.
		if (!isCanonical(request.getRequestURI())) {
			resolver.resolveException(request, response, null,
					new ApiException(HttpStatus.BAD_REQUEST, CODE, "The request path is not in its canonical form"));
			return;
		}
		chain.doFilter(request, response);
	}

}
