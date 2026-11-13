package com.zawed.api.platform.security;

import java.util.Collection;
import java.util.List;
import java.util.UUID;
import java.util.stream.Stream;

import org.springframework.security.authentication.AbstractAuthenticationToken;
import org.springframework.security.core.GrantedAuthority;
import org.springframework.security.core.authority.SimpleGrantedAuthority;

/**
 * The signed-in user of the current request, built from the access token and the session row,
 * which is read on every request. Everything here is what the database says now, not what the
 * token said when it was issued.
 */
public record ZawedPrincipal(UUID userId, UUID sessionId, Role role, Scope scope, boolean mustChangePassword) {

	public boolean can(Permission permission) {
		return Permission.of(role).contains(permission);
	}

	public Authentication toAuthentication() {
		return new Authentication(this);
	}

	/** The Spring Security authentication for a principal. */
	public static final class Authentication extends AbstractAuthenticationToken {

		private final ZawedPrincipal principal;

		Authentication(ZawedPrincipal principal) {
			super(authoritiesOf(principal));
			this.principal = principal;
			setAuthenticated(true);
		}

		private static Collection<? extends GrantedAuthority> authoritiesOf(ZawedPrincipal principal) {
			return Stream
				.concat(Stream.of("ROLE_" + principal.role().name()),
						Permission.of(principal.role()).stream().map(Permission::authority))
				.map(SimpleGrantedAuthority::new)
				.toList();
		}

		@Override
		public Object getCredentials() {
			return "";
		}

		@Override
		public ZawedPrincipal getPrincipal() {
			return principal;
		}

		@Override
		public String getName() {
			return principal.userId().toString();
		}

		@Override
		public List<GrantedAuthority> getAuthorities() {
			return List.copyOf(super.getAuthorities());
		}

	}

}
