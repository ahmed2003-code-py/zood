package com.zawed.api.network;

import java.util.List;
import java.util.UUID;

import org.springframework.security.authentication.AbstractAuthenticationToken;
import org.springframework.security.core.authority.SimpleGrantedAuthority;

import com.zawed.api.platform.security.PosAuthenticator;

/**
 * Who is behind a POS request: an ACTIVE device at one station, and the agent signed in on it
 * (null on the agent sign-in call itself). Both are read from the database on every request, so a
 * revoked device or a suspended agent stops at once (B4 contract §1).
 */
public record PosPrincipal(UUID deviceId, String deviceCode, UUID stationId, UUID operatorId, UUID agentId,
		String agentWorkerId, String agentName, boolean mustChangePin) {

	public boolean hasAgent() {
		return agentId != null;
	}

	AbstractAuthenticationToken toAuthentication() {
		List<SimpleGrantedAuthority> authorities = hasAgent()
				? List.of(new SimpleGrantedAuthority(PosAuthenticator.DEVICE), new SimpleGrantedAuthority(
						mustChangePin ? PosAuthenticator.AGENT_PIN_CHANGE : PosAuthenticator.AGENT))
				: List.of(new SimpleGrantedAuthority(PosAuthenticator.DEVICE));
		PosPrincipal self = this;
		AbstractAuthenticationToken token = new AbstractAuthenticationToken(authorities) {

			@Override
			public Object getCredentials() {
				return "";
			}

			@Override
			public Object getPrincipal() {
				return self;
			}

		};
		token.setAuthenticated(true);
		return token;
	}

}
