package com.zawed.api.platform.security;

import java.util.List;

import jakarta.servlet.DispatcherType;

import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.http.HttpMethod;
import org.springframework.security.config.Customizer;
import org.springframework.security.config.annotation.web.builders.HttpSecurity;
import org.springframework.security.config.annotation.web.configuration.EnableWebSecurity;
import org.springframework.security.config.http.SessionCreationPolicy;
import org.springframework.security.core.userdetails.UserDetailsService;
import org.springframework.security.core.userdetails.UsernameNotFoundException;
import org.springframework.security.web.SecurityFilterChain;
import org.springframework.web.cors.CorsConfiguration;
import org.springframework.web.cors.CorsConfigurationSource;
import org.springframework.web.cors.UrlBasedCorsConfigurationSource;

import com.zawed.api.platform.config.ZawedProperties;
import com.zawed.api.platform.web.RequestIdFilter;

/**
 * Deny by default. A route is reachable only when it is listed here as public; everything else is
 * refused before it reaches a controller, including routes that do not exist, so a refusal says
 * nothing about what the API contains.
 *
 * <p>There is no way to sign in yet: authentication arrives in phase B1 and replaces
 * {@code denyAll()} with {@code authenticated()} plus per-action permissions.
 */
@Configuration(proxyBeanMethods = false)
@EnableWebSecurity
class SecurityConfig {

	/** Read-only routes that need no identity: health probes, service metadata, the API description. */
	private static final String[] PUBLIC_GET = { "/api/v1/health", "/api/v1/health/liveness",
			"/api/v1/health/readiness", "/api/v1/meta", "/api/v1/openapi.json" };

	@Bean
	SecurityFilterChain apiSecurity(HttpSecurity http, SecurityErrorHandler errors) throws Exception {
		http
			// The API is stateless and takes its credentials from a header, never from a cookie the
			// browser attaches by itself, so there is no request for CSRF protection to defend. The
			// refresh cookie that arrives with B1 gets its own protection on its own route.
			.csrf((csrf) -> csrf.disable())
			.cors(Customizer.withDefaults())
			.sessionManagement((session) -> session.sessionCreationPolicy(SessionCreationPolicy.STATELESS))
			.formLogin((form) -> form.disable())
			.httpBasic((basic) -> basic.disable())
			.logout((logout) -> logout.disable())
			.requestCache((cache) -> cache.disable())
			.authorizeHttpRequests((requests) -> requests
				// Lets the container's error dispatch reach the error controller.
				.dispatcherTypeMatchers(DispatcherType.ERROR)
				.permitAll()
				.requestMatchers(HttpMethod.GET, PUBLIC_GET)
				.permitAll()
				.anyRequest()
				.denyAll())
			.exceptionHandling((handling) -> handling.authenticationEntryPoint(errors).accessDeniedHandler(errors));
		return http.build();
	}

	@Bean
	CorsConfigurationSource corsConfigurationSource(ZawedProperties properties) {
		CorsConfiguration cors = new CorsConfiguration();
		cors.setAllowedOrigins(properties.cors().allowedOrigins());
		cors.setAllowedMethods(List.of("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"));
		cors.setAllowedHeaders(List.of("Authorization", "Content-Type", "Accept", "Idempotency-Key",
				RequestIdFilter.HEADER));
		cors.setExposedHeaders(List.of(RequestIdFilter.HEADER, "Retry-After"));
		cors.setAllowCredentials(true);
		cors.setMaxAge(3600L);
		UrlBasedCorsConfigurationSource source = new UrlBasedCorsConfigurationSource();
		source.registerCorsConfiguration("/api/**", cors);
		return source;
	}

	/**
	 * No users exist until B1. Declaring this stops Spring Boot from creating a default user with a
	 * generated password, which would be a working credential nobody asked for.
	 */
	@Bean
	UserDetailsService noUsers() {
		return (username) -> {
			throw new UsernameNotFoundException("No users are defined");
		};
	}

}
