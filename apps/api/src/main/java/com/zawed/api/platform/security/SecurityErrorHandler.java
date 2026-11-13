package com.zawed.api.platform.security;

import java.io.IOException;

import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;

import org.springframework.beans.factory.annotation.Qualifier;
import org.springframework.http.HttpStatus;
import org.springframework.security.access.AccessDeniedException;
import org.springframework.security.core.Authentication;
import org.springframework.security.core.context.SecurityContextHolder;
import org.springframework.security.core.AuthenticationException;
import org.springframework.security.web.AuthenticationEntryPoint;
import org.springframework.security.web.access.AccessDeniedHandler;
import org.springframework.stereotype.Component;
import org.springframework.web.servlet.HandlerExceptionResolver;

import com.zawed.api.platform.web.ApiException;

/**
 * Security refuses a request in the filter chain, before any controller exists to throw from. This
 * hands that refusal to the same exception handling controllers use, so a 401 or 403 from security
 * has the same body as every other error.
 */
@Component
class SecurityErrorHandler implements AuthenticationEntryPoint, AccessDeniedHandler {

	private final HandlerExceptionResolver resolver;

	SecurityErrorHandler(@Qualifier("handlerExceptionResolver") HandlerExceptionResolver resolver) {
		this.resolver = resolver;
	}

	@Override
	public void commence(HttpServletRequest request, HttpServletResponse response, AuthenticationException ex)
			throws IOException {
		if (resolver.resolveException(request, response, null, ex) == null) {
			response.sendError(HttpServletResponse.SC_UNAUTHORIZED);
		}
	}

	/**
	 * The POS chain's refusal of a signed-in device: it tells the terminal what to show next (the
	 * agent sign-in, the PIN change) instead of a bare 403.
	 */
	AccessDeniedHandler posAccessDenied() {
		return (request, response, ex) -> {
			Authentication authentication = SecurityContextHolder.getContext().getAuthentication();
			ApiException refusal = has(authentication, PosAuthenticator.AGENT_PIN_CHANGE)
					? new ApiException(HttpStatus.FORBIDDEN, "PIN_CHANGE_REQUIRED", "Change the PIN first")
					: has(authentication, PosAuthenticator.DEVICE) && !has(authentication, PosAuthenticator.AGENT)
							? new ApiException(HttpStatus.UNAUTHORIZED, "AGENT_SESSION_REQUIRED", "An agent must sign in")
							: null;
			if (refusal == null) {
				handle(request, response, ex);
			}
			else if (resolver.resolveException(request, response, null, refusal) == null) {
				response.sendError(refusal.status().value());
			}
		};
	}

	private static boolean has(Authentication authentication, String authority) {
		return authentication != null
				&& authentication.getAuthorities().stream().anyMatch((a) -> authority.equals(a.getAuthority()));
	}

	@Override
	public void handle(HttpServletRequest request, HttpServletResponse response, AccessDeniedException ex)
			throws IOException {
		if (resolver.resolveException(request, response, null, ex) == null) {
			response.sendError(HttpServletResponse.SC_FORBIDDEN);
		}
	}

}
