package com.zawed.api.platform.security;

import java.time.Duration;
import java.util.List;
import java.util.Set;
import java.util.UUID;

import javax.crypto.spec.SecretKeySpec;
import javax.sql.DataSource;

import jakarta.servlet.DispatcherType;

import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.core.annotation.Order;
import org.springframework.core.convert.converter.Converter;
import org.springframework.http.HttpMethod;
import org.springframework.security.authentication.AbstractAuthenticationToken;
import org.springframework.security.config.Customizer;
import org.springframework.security.config.annotation.method.configuration.EnableMethodSecurity;
import org.springframework.security.config.annotation.web.builders.HttpSecurity;
import org.springframework.security.config.annotation.web.configuration.EnableWebSecurity;
import org.springframework.security.config.http.SessionCreationPolicy;
import org.springframework.security.core.annotation.AnnotationTemplateExpressionDefaults;
import org.springframework.security.core.userdetails.UserDetailsService;
import org.springframework.security.core.userdetails.UsernameNotFoundException;
import org.springframework.security.oauth2.core.DelegatingOAuth2TokenValidator;
import org.springframework.security.oauth2.core.OAuth2Error;
import org.springframework.security.oauth2.core.OAuth2TokenValidator;
import org.springframework.security.oauth2.core.OAuth2TokenValidatorResult;
import org.springframework.security.oauth2.jose.jws.MacAlgorithm;
import org.springframework.security.oauth2.jwt.Jwt;
import org.springframework.security.oauth2.jwt.JwtDecoder;
import org.springframework.security.oauth2.jwt.JwtIssuerValidator;
import org.springframework.security.oauth2.jwt.JwtTimestampValidator;
import org.springframework.security.oauth2.jwt.NimbusJwtDecoder;
import org.springframework.security.oauth2.server.resource.InvalidBearerTokenException;
import org.springframework.security.oauth2.server.resource.web.authentication.BearerTokenAuthenticationFilter;
import org.springframework.security.web.SecurityFilterChain;
import org.springframework.security.web.authentication.AnonymousAuthenticationFilter;
import org.springframework.web.cors.CorsConfiguration;
import org.springframework.web.cors.CorsConfigurationSource;
import org.springframework.web.cors.UrlBasedCorsConfigurationSource;

import com.zawed.api.platform.config.ZawedProperties;
import com.zawed.api.platform.ratelimit.RateLimiter;
import com.zawed.api.platform.web.RequestIdFilter;

/**
 * Deny by default. A route is reachable only when it is listed here as public or the caller holds a
 * valid access token; everything else is refused before it reaches a controller, including routes
 * that do not exist, so a refusal says nothing about what the API contains. What a signed-in user
 * may do is decided per endpoint by {@link RequiresPermission}.
 */
@Configuration(proxyBeanMethods = false)
@EnableWebSecurity
@EnableMethodSecurity
public class SecurityConfig {

	/** Issuer of access tokens; checked on every token. */
	public static final String TOKEN_ISSUER = "zawed-api";

	/** The {@code typ} claim of a user access token; device tokens carry {@code device}. */
	public static final String USER_TOKEN_TYPE = "user";

	/** Read-only routes that need no identity: health probes, service metadata, the API description. */
	private static final String[] PUBLIC_GET = { "/api/v1/health", "/api/v1/health/liveness",
			"/api/v1/health/readiness", "/api/v1/meta", "/api/v1/openapi.json",
			// A receipt link is its own proof: a keyed signature of the fueling id (fueling.Receipts).
			"/api/v1/receipts/*" };

	/** The sign-in path. Each of these protects itself (rate limits, CSRF rule, constant-time checks). */
	private static final String[] PUBLIC_POST = { "/api/v1/auth/login", "/api/v1/auth/mfa/verify",
			"/api/v1/auth/mfa/enroll/start", "/api/v1/auth/mfa/enroll/confirm", "/api/v1/auth/refresh",
			"/api/v1/auth/logout", "/api/v1/admin/auth/login", "/api/v1/admin/auth/refresh",
			"/api/v1/admin/auth/logout", "/api/v1/setup/super-admin" };

	/** The two POS calls that obtain credentials; each checks its own proof (B4 contract §1). */
	static final Set<String> POS_PUBLIC_POST = Set.of("/api/v1/pos/activate", "/api/v1/pos/token");

	/**
	 * The POS terminal's chain, checked before the users' one. Devices hold no user token: a request
	 * here is identified by a device token (and an agent session), never by a user's access token,
	 * so a user token is refused on POS routes and a device token everywhere else.
	 */
	@Bean
	@Order(0)
	SecurityFilterChain posSecurity(HttpSecurity http, SecurityErrorHandler errors, PosAuthenticator pos,
			SecurityFilters filters) throws Exception {
		http.securityMatcher("/api/v1/pos/**")
			.csrf((csrf) -> csrf.disable())
			.cors(Customizer.withDefaults())
			.sessionManagement((session) -> session.sessionCreationPolicy(SessionCreationPolicy.STATELESS))
			.formLogin((form) -> form.disable())
			.httpBasic((basic) -> basic.disable())
			.logout((logout) -> logout.disable())
			.requestCache((cache) -> cache.disable())
			.anonymous(Customizer.withDefaults())
			.authorizeHttpRequests((requests) -> requests.dispatcherTypeMatchers(DispatcherType.ERROR)
				.permitAll()
				.requestMatchers(HttpMethod.POST, POS_PUBLIC_POST.toArray(String[]::new))
				.permitAll()
				.requestMatchers(HttpMethod.POST, "/api/v1/pos/agents/login")
				.hasAuthority(PosAuthenticator.DEVICE)
				.requestMatchers(HttpMethod.POST, "/api/v1/pos/agents/pin", "/api/v1/pos/agents/logout")
				.hasAnyAuthority(PosAuthenticator.AGENT, PosAuthenticator.AGENT_PIN_CHANGE)
				.anyRequest()
				.hasAuthority(PosAuthenticator.AGENT))
			.addFilterBefore(filters.posAuthentication(pos, POS_PUBLIC_POST), AnonymousAuthenticationFilter.class)
			.exceptionHandling((handling) -> handling.authenticationEntryPoint(errors)
				.accessDeniedHandler(errors.posAccessDenied()));
		return http.build();
	}

	@Bean
	SecurityFilterChain apiSecurity(HttpSecurity http, SecurityErrorHandler errors, JwtDecoder jwtDecoder,
			SessionVerifier sessions, RateLimiter rateLimiter, SecurityFilters filters) throws Exception {
		http
			// Access tokens travel in the Authorization header, which a browser never attaches by
			// itself, so ordinary endpoints need no CSRF token. The two endpoints that read the
			// refresh cookie (refresh, logout) check Origin and a custom header themselves.
			.csrf((csrf) -> csrf.disable())
			.cors(Customizer.withDefaults())
			.sessionManagement((session) -> session.sessionCreationPolicy(SessionCreationPolicy.STATELESS))
			.formLogin((form) -> form.disable())
			.httpBasic((basic) -> basic.disable())
			.logout((logout) -> logout.disable())
			.requestCache((cache) -> cache.disable())
			.anonymous(Customizer.withDefaults())
			.authorizeHttpRequests((requests) -> requests
				// Lets the container's error dispatch reach the error controller.
				.dispatcherTypeMatchers(DispatcherType.ERROR)
				.permitAll()
				.requestMatchers(HttpMethod.GET, PUBLIC_GET)
				.permitAll()
				.requestMatchers(HttpMethod.POST, PUBLIC_POST)
				.permitAll()
				.requestMatchers("/api/v1/**")
				.authenticated()
				.anyRequest()
				.denyAll())
			.oauth2ResourceServer((resource) -> resource.authenticationEntryPoint(errors)
				.accessDeniedHandler(errors)
				.jwt((jwt) -> jwt.decoder(jwtDecoder).jwtAuthenticationConverter(sessionChecked(sessions))))
			.addFilterAfter(filters.passwordChangeGate(), BearerTokenAuthenticationFilter.class)
			.addFilterAfter(filters.rateLimits(rateLimiter), BearerTokenAuthenticationFilter.class)
			.exceptionHandling((handling) -> handling.authenticationEntryPoint(errors).accessDeniedHandler(errors));
		return http.build();
	}

	/**
	 * HS256 only (the algorithm is pinned, a token naming any other is refused), our issuer, a valid
	 * time window, and the claims this API relies on.
	 */
	@Bean
	JwtDecoder jwtDecoder(ZawedProperties properties) {
		NimbusJwtDecoder decoder = NimbusJwtDecoder
			.withSecretKey(new SecretKeySpec(properties.security().jwtSecretBytes(), "HmacSHA256"))
			.macAlgorithm(MacAlgorithm.HS256)
			.build();
		decoder.setJwtValidator(new DelegatingOAuth2TokenValidator<>(new JwtTimestampValidator(Duration.ofSeconds(30)),
				new JwtIssuerValidator(TOKEN_ISSUER), userTokenClaims()));
		return decoder;
	}

	private static OAuth2TokenValidator<Jwt> userTokenClaims() {
		OAuth2Error invalid = new OAuth2Error("invalid_token", "Not a user access token", null);
		return (jwt) -> USER_TOKEN_TYPE.equals(jwt.getClaimAsString("typ")) && jwt.getClaimAsString("sid") != null
				&& jwt.getSubject() != null ? OAuth2TokenValidatorResult.success()
						: OAuth2TokenValidatorResult.failure(invalid);
	}

	/**
	 * A valid signature is not enough: the session the token names must still be live, and the user
	 * still active. Role and scope come from the database now, not from the token.
	 */
	private static Converter<Jwt, AbstractAuthenticationToken> sessionChecked(SessionVerifier sessions) {
		return (jwt) -> {
			UUID sessionId;
			UUID userId;
			try {
				sessionId = UUID.fromString(jwt.getClaimAsString("sid"));
				userId = UUID.fromString(jwt.getSubject());
			}
			catch (IllegalArgumentException ex) {
				throw new InvalidBearerTokenException("Malformed token claims");
			}
			return sessions.verify(sessionId, userId)
				.map(ZawedPrincipal::toAuthentication)
				.orElseThrow(() -> new InvalidBearerTokenException("The session is no longer valid"));
		};
	}

	/** Lets {@link RequiresPermission} name a permission enum instead of a SpEL string. */
	@Bean
	static AnnotationTemplateExpressionDefaults templateExpressionDefaults() {
		return new AnnotationTemplateExpressionDefaults();
	}

	/** Every transaction tells PostgreSQL whose data it is (row-level security). */
	@Bean
	ScopedTransactionManager transactionManager(DataSource dataSource) {
		return new ScopedTransactionManager(dataSource);
	}

	@Bean
	CorsConfigurationSource corsConfigurationSource(ZawedProperties properties) {
		CorsConfiguration cors = new CorsConfiguration();
		cors.setAllowedOrigins(properties.cors().allowedOrigins());
		cors.setAllowedMethods(List.of("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"));
		cors.setAllowedHeaders(List.of("Authorization", "Content-Type", "Accept", "Idempotency-Key",
				RequestIdFilter.HEADER, CsrfRule.CLIENT_HEADER, "X-Agent-Session"));
		cors.setExposedHeaders(List.of(RequestIdFilter.HEADER, "Retry-After"));
		cors.setAllowCredentials(true);
		cors.setMaxAge(3600L);
		UrlBasedCorsConfigurationSource source = new UrlBasedCorsConfigurationSource();
		source.registerCorsConfiguration("/api/**", cors);
		return source;
	}

	/**
	 * Sign-in is the identity module's own code, not Spring's username/password flow. Declaring this
	 * stops Spring Boot from creating a default user with a generated password.
	 */
	@Bean
	UserDetailsService noUserDetails() {
		return (username) -> {
			throw new UsernameNotFoundException("Not used");
		};
	}

}
