package com.zawed.api.platform.security;

import java.util.List;

import jakarta.servlet.http.HttpServletRequest;

import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Component;

import com.zawed.api.platform.config.ZawedProperties;
import com.zawed.api.platform.web.ApiException;

/**
 * The defence for the endpoints that act on the refresh cookie (refresh, logout).
 *
 * <p>The cookie is SameSite=Strict, so another site cannot make the browser send it. As a second,
 * independent layer, these endpoints also require the browser's {@code Origin} to be one of ours and
 * a custom header that a cross-site form or image request cannot set.
 */
@Component
public class CsrfRule {

	public static final String CLIENT_HEADER = "X-Zawed-Client";

	private final List<String> allowedOrigins;

	CsrfRule(ZawedProperties properties) {
		this.allowedOrigins = properties.cors().allowedOrigins();
	}

	public void check(HttpServletRequest request) {
		String origin = request.getHeader("Origin");
		if (!"web".equals(request.getHeader(CLIENT_HEADER)) || origin == null || !allowedOrigins.contains(origin)) {
			throw new ApiException(HttpStatus.FORBIDDEN, "CSRF_REJECTED", "Request origin not accepted");
		}
	}

}
