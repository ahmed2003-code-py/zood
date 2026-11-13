package com.zawed.api.network;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.Optional;
import java.util.UUID;

import javax.crypto.Mac;
import javax.crypto.SecretKey;
import javax.crypto.spec.SecretKeySpec;

import com.nimbusds.jose.jwk.source.ImmutableSecret;

import org.springframework.security.oauth2.core.DelegatingOAuth2TokenValidator;
import org.springframework.security.oauth2.core.OAuth2Error;
import org.springframework.security.oauth2.core.OAuth2TokenValidatorResult;
import org.springframework.security.oauth2.jose.jws.MacAlgorithm;
import org.springframework.security.oauth2.jwt.JwsHeader;
import org.springframework.security.oauth2.jwt.Jwt;
import org.springframework.security.oauth2.jwt.JwtClaimsSet;
import org.springframework.security.oauth2.jwt.JwtEncoderParameters;
import org.springframework.security.oauth2.jwt.JwtException;
import org.springframework.security.oauth2.jwt.JwtIssuerValidator;
import org.springframework.security.oauth2.jwt.JwtTimestampValidator;
import org.springframework.security.oauth2.jwt.NimbusJwtDecoder;
import org.springframework.security.oauth2.jwt.NimbusJwtEncoder;
import org.springframework.stereotype.Component;

import com.zawed.api.platform.config.ZawedProperties;
import com.zawed.api.platform.security.SecurityConfig;

/**
 * Device tokens (ADR 0010): short-lived HS256 JWTs with {@code typ=device}, signed with a key
 * derived from the JWT secret under its own label. A device token therefore fails a user token's
 * signature check, and a user token fails a device token's, before either type check runs: the two
 * can never stand in for each other. The token names the device only; whether the device is still
 * active is read from the database on every request.
 */
@Component
class DeviceTokens {

	static final String DEVICE_TOKEN_TYPE = "device";
	static final Duration LIFETIME = Duration.ofMinutes(15);

	private final NimbusJwtEncoder encoder;
	private final NimbusJwtDecoder decoder;
	private final Clock clock;

	DeviceTokens(ZawedProperties properties, Clock clock) {
		SecretKey key = derivedKey(properties.security().jwtSecretBytes());
		this.encoder = new NimbusJwtEncoder(new ImmutableSecret<>(key));
		this.decoder = NimbusJwtDecoder.withSecretKey(key).macAlgorithm(MacAlgorithm.HS256).build();
		OAuth2Error notDevice = new OAuth2Error("invalid_token", "Not a device token", null);
		this.decoder.setJwtValidator(new DelegatingOAuth2TokenValidator<>(new JwtTimestampValidator(Duration.ofSeconds(30)),
				new JwtIssuerValidator(SecurityConfig.TOKEN_ISSUER),
				(jwt) -> DEVICE_TOKEN_TYPE.equals(jwt.getClaimAsString("typ")) && jwt.getSubject() != null
						? OAuth2TokenValidatorResult.success() : OAuth2TokenValidatorResult.failure(notDevice)));
		this.clock = clock;
	}

	record Issued(String token, Instant expiresAt) {
	}

	Issued issue(UUID deviceId) {
		Instant now = clock.instant();
		Instant expires = now.plus(LIFETIME);
		JwtClaimsSet claims = JwtClaimsSet.builder()
			.issuer(SecurityConfig.TOKEN_ISSUER)
			.subject(deviceId.toString())
			.id(UUID.randomUUID().toString())
			.issuedAt(now)
			.expiresAt(expires)
			.claim("typ", DEVICE_TOKEN_TYPE)
			.build();
		String token = encoder.encode(JwtEncoderParameters.from(JwsHeader.with(MacAlgorithm.HS256).build(), claims))
			.getTokenValue();
		return new Issued(token, expires);
	}

	/** The device a token names, or empty for any token that is not a valid, current device token. */
	Optional<UUID> deviceOf(String token) {
		try {
			Jwt jwt = decoder.decode(token);
			return Optional.of(UUID.fromString(jwt.getSubject()));
		}
		catch (JwtException | IllegalArgumentException ex) {
			return Optional.empty();
		}
	}

	private static SecretKey derivedKey(byte[] jwtSecret) {
		try {
			Mac mac = Mac.getInstance("HmacSHA256");
			mac.init(new SecretKeySpec(jwtSecret, "HmacSHA256"));
			return new SecretKeySpec(mac.doFinal("zawed-device-token-v1".getBytes(StandardCharsets.UTF_8)), "HmacSHA256");
		}
		catch (GeneralSecurityException ex) {
			throw new IllegalStateException(ex);
		}
	}

}
