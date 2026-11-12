package com.zawed.api.platform.web;

import java.io.IOException;
import java.util.UUID;
import java.util.regex.Pattern;

import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;

import org.slf4j.MDC;
import org.springframework.core.Ordered;
import org.springframework.core.annotation.Order;
import org.springframework.stereotype.Component;
import org.springframework.web.filter.OncePerRequestFilter;

/**
 * Gives every request an id, puts it on every log line written while the request is handled, and
 * returns it as {@code X-Request-Id}, so a failure a user reports can be found in the logs.
 *
 * <p>It runs before the security filters: a request that security rejects is exactly the kind that
 * needs an id. An inbound id is kept only when it is short and plain, because it ends up in log
 * lines, and an unchecked header is how a forged line gets into a log.
 */
@Component
@Order(Ordered.HIGHEST_PRECEDENCE)
public class RequestIdFilter extends OncePerRequestFilter {

	public static final String HEADER = "X-Request-Id";
	public static final String MDC_KEY = "requestId";
	public static final String ATTRIBUTE = RequestIdFilter.class.getName() + ".requestId";

	private static final Pattern SAFE_ID = Pattern.compile("[A-Za-z0-9._-]{1,64}");

	@Override
	protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain chain)
			throws ServletException, IOException {
		// The container's error dispatch for the same request runs this filter again: keep the id the
		// first dispatch chose, so the response header and every log line agree.
		String requestId = currentRequestId(request);
		if (requestId == null) {
			String inbound = request.getHeader(HEADER);
			requestId = inbound != null && SAFE_ID.matcher(inbound).matches() ? inbound : UUID.randomUUID().toString();
		}
		request.setAttribute(ATTRIBUTE, requestId);
		response.setHeader(HEADER, requestId);
		MDC.put(MDC_KEY, requestId);
		try {
			chain.doFilter(request, response);
		}
		finally {
			MDC.remove(MDC_KEY);
		}
	}

	/** Also on the error dispatch, so log lines written while the error is answered carry the id. */
	@Override
	protected boolean shouldNotFilterErrorDispatch() {
		return false;
	}

	/** The id of the request being handled, or null outside a request. */
	public static String currentRequestId(HttpServletRequest request) {
		return request.getAttribute(ATTRIBUTE) instanceof String id ? id : null;
	}

}
