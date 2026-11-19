package com.zawed.api.platform.security;

/**
 * Web portal roles (PRD §3, contract §2). Station agents and drivers are not web users. OPS_MANAGER
 * (V12, ADR 0021) is Operations plus the approval of money entering wallets: the checker of the
 * maker-checker rule, so a plain Ops user can verify a transfer but never approve one.
 */
public enum Role {

	SUPER_ADMIN(ScopeType.PLATFORM), OPS_MANAGER(ScopeType.PLATFORM), OPS(ScopeType.PLATFORM), FLEET_MANAGER(ScopeType.COMPANY),
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

	/**
	 * Roles that must use a second factor from the first sign-in (ADR 0009): Operations and Super
	 * Admin since B1, and since B6 the Station Manager, who reads the station's bank account and
	 * payouts (roadmap delta A3). A user of such a role who has no authenticator yet is asked to
	 * enrol one at the next sign-in.
	 */
	public boolean requiresMfa() {
		return this == SUPER_ADMIN || this == OPS_MANAGER || this == OPS || this == STATION_MANAGER;
	}

}
