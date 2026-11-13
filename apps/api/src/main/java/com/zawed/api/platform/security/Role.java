package com.zawed.api.platform.security;

/** Web portal roles (PRD §3, contract §2). Station agents and drivers are not web users. */
public enum Role {

	SUPER_ADMIN(ScopeType.PLATFORM), OPS(ScopeType.PLATFORM), FLEET_MANAGER(ScopeType.COMPANY),
	ACCOUNTANT(ScopeType.COMPANY), STATION_MANAGER(ScopeType.OPERATOR);

	private final ScopeType scopeType;

	Role(ScopeType scopeType) {
		this.scopeType = scopeType;
	}

	/** The only kind of scope a user with this role can have. */
	public ScopeType scopeType() {
		return scopeType;
	}

	public boolean isPlatform() {
		return scopeType == ScopeType.PLATFORM;
	}

	/** Roles that must use a second factor from the first sign-in (ADR 0009). */
	public boolean requiresMfa() {
		return this == SUPER_ADMIN || this == OPS;
	}

}
