package com.zawed.api.leads;

import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;

import org.springframework.context.annotation.Configuration;
import org.springframework.http.HttpStatus;
import org.springframework.web.method.HandlerMethod;
import org.springframework.web.servlet.HandlerInterceptor;
import org.springframework.web.servlet.config.annotation.InterceptorRegistry;
import org.springframework.web.servlet.config.annotation.WebMvcConfigurer;

import com.zawed.api.platform.web.ApiException;

/**
 * The second half of {@link DemoRequestGate}: the demo-request handler runs only for a request the gate
 * counted and read. The gate matches paths; this matches the handler Spring chose, so a spelling of
 * the path the gate does not recognise is refused (400) instead of reaching the controller without the
 * per-address limit and the body cap. It runs before the body is parsed.
 */
@Configuration(proxyBeanMethods = false)
class LeadsWebConfig implements WebMvcConfigurer {

	@Override
	public void addInterceptors(InterceptorRegistry registry) {
		registry.addInterceptor(new GateRequired());
	}

	static final class GateRequired implements HandlerInterceptor {

		@Override
		public boolean preHandle(HttpServletRequest request, HttpServletResponse response, Object handler) {
			if (handler instanceof HandlerMethod method && method.getBeanType() == LeadsController.class
					&& method.getMethod().getName().equals("requestDemo")
					&& request.getAttribute(DemoRequestGate.PASSED) == null) {
				throw new ApiException(HttpStatus.BAD_REQUEST, "BAD_REQUEST", "Bad Request");
			}
			return true;
		}

	}

}
