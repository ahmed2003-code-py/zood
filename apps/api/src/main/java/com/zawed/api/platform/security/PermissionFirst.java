package com.zawed.api.platform.security;

import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;

import org.springframework.context.annotation.Configuration;
import org.springframework.core.Ordered;
import org.springframework.core.annotation.AnnotatedElementUtils;
import org.springframework.security.access.AccessDeniedException;
import org.springframework.security.core.Authentication;
import org.springframework.security.core.GrantedAuthority;
import org.springframework.security.core.context.SecurityContextHolder;
import org.springframework.web.method.HandlerMethod;
import org.springframework.web.servlet.HandlerInterceptor;
import org.springframework.web.servlet.config.annotation.InterceptorRegistry;
import org.springframework.web.servlet.config.annotation.WebMvcConfigurer;

/**
 * The permission of an endpoint is checked before its arguments are resolved (QA round 1).
 *
 * <p>{@link RequiresPermission} is method security: it runs when the controller method is called,
 * which is after Spring MVC has read the body, converted the path variables and run bean
 * validation. A caller without the permission therefore got 400 with the request's field names,
 * which tells a station token what an Ops payout looks like. This interceptor runs once the
 * handler is chosen and before any argument is read, and refuses with the same 403 as method
 * security. The annotation on the method stays the single statement of the rule, and method
 * security still runs after this check as the second line.
 */
@Configuration(proxyBeanMethods = false)
class PermissionFirst implements WebMvcConfigurer {

	@Override
	public void addInterceptors(InterceptorRegistry registry) {
		// First, so no other interceptor reads the request for a caller who may not call the endpoint.
		registry.addInterceptor(new Check()).order(Ordered.HIGHEST_PRECEDENCE);
	}

	static final class Check implements HandlerInterceptor {

		@Override
		public boolean preHandle(HttpServletRequest request, HttpServletResponse response, Object handler) {
			if (!(handler instanceof HandlerMethod method)) {
				return true;
			}
			RequiresPermission required = AnnotatedElementUtils.findMergedAnnotation(method.getMethod(),
					RequiresPermission.class);
			if (required == null) {
				required = AnnotatedElementUtils.findMergedAnnotation(method.getBeanType(), RequiresPermission.class);
			}
			if (required == null) {
				return true;
			}
			String authority = required.value().authority();
			Authentication authentication = SecurityContextHolder.getContext().getAuthentication();
			boolean granted = authentication != null && authentication.isAuthenticated()
					&& authentication.getAuthorities().stream().map(GrantedAuthority::getAuthority).anyMatch(authority::equals);
			if (!granted) {
				throw new AccessDeniedException("Missing " + authority);
			}
			if (required.scope().length > 0 && !(authentication.getPrincipal() instanceof ZawedPrincipal principal
					&& java.util.Arrays.asList(required.scope()).contains(principal.scope().type()))) {
				throw new AccessDeniedException("Not allowed for this kind of scope");
			}
			return true;
		}

	}

}
