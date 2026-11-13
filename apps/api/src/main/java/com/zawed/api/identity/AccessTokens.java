package com.zawed.api.identity;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;

import javax.crypto.spec.SecretKeySpec;

import com.nimbusds.jose.jwk.source.ImmutableSecret;

import org.springframework.security.oauth2.jose.jws.MacAlgorithm;
import org.springframework.security.oauth2.jwt.JwsHeader;
import org.springframework.security.oauth2.jwt.JwtClaimsSet;
import org.springframework.security.oauth2.jwt.JwtEncoder;
import org.springframework.security.oauth2.jwt.JwtEncoderParameters;
import org.springframework.security.oauth2.jwt.NimbusJwtEncoder;
import org.springframework.stereotype.Component;

import com.zawed.api.platform.config.ZawedProperties;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.SecurityConfig;

/**
 * Issues access tokens: short-lived HS256 JWTs. They are only an index into the session table,
 * which is read on every request; role and scope in the token are informational for clients.
 */
@Component
class AccessTokens {

	static final Duration LIFETIME = Duration.ofMinutes(15);

	private final JwtEncoder encoder;
	private final Clock clock;

	AccessTokens(ZawedProperties properties, Clock clock) {
		SecretKeySpec key = new SecretKeySpec(properties.security().jwtSecretBytes(), "HmacSHA256");
		this.encoder = new NimbusJwtEncoder(new ImmutableSecret<>(key));
		this.clock = clock;
	}

	String issue(UUID userId, UUID sessionId, Role role, Scope scope) {
		Instant now = clock.instant();
		Map<String, Object> scopeClaim = new LinkedHashMap<>();
		scopeClaim.put("type", scope.type().name());
		if (scope.id() != null) {
			scopeClaim.put("id", scope.id().toString());
		}
		JwtClaimsSet claims = JwtClaimsSet.builder()
			.issuer(SecurityConfig.TOKEN_ISSUER)
			.subject(userId.toString())
			.id(UUID.randomUUID().toString())
			.issuedAt(now)
			.expiresAt(now.plus(LIFETIME))
			.claim("typ", SecurityConfig.USER_TOKEN_TYPE)
			.claim("sid", sessionId.toString())
			.claim("role", role.name())
			.claim("scope", scopeClaim)
			.build();
		return encoder.encode(JwtEncoderParameters.from(JwsHeader.with(MacAlgorithm.HS256).build(), claims))
			.getTokenValue();
	}

}
