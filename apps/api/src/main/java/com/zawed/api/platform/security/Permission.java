package com.zawed.api.platform.security;

import java.util.EnumSet;
import java.util.Map;
import java.util.Set;

/**
 * What a role may do. The single source of truth for authorization: endpoints declare the
 * permission they need with {@link RequiresPermission}, and nothing else grants access.
 * {@code AuthorizationMatrixTests} checks every endpoint against every role.
 */
public enum Permission {

	// ---------------------------------------------------------------------------------- B1 identity

	/** List and read platform and scoped users. Ops sees scoped roles only (enforced in the service). */
	USERS_READ,
	/** Create users. */
	USERS_CREATE,
	/** Suspend, deactivate or reactivate a user. */
	USERS_STATUS,
	/** Issue a temporary password. Ops: scoped roles only. */
	USERS_RESET_PASSWORD,
	/** Clear a lockout. Ops: scoped roles only. */
	USERS_UNLOCK,
	/** Clear a user's second factor so the next sign-in re-enrols. */
	USERS_MFA_RESET,
	/** Read the audit log. */
	AUDIT_READ,

	// ----------------------------------------------------------------------------- B2 master data

	/** Read any company, fleet, station, device, agent or price (the Ops console). */
	OPS_READ,
	/** Onboard and edit companies and their departments; freeze and unfreeze. */
	COMPANIES_MANAGE,
	/**
	 * A company's financial and control settings: cooldown window and low-balance threshold.
	 * Super Admin only (PRD §3: Ops may not modify financial configuration without Super Admin).
	 */
	COMPANY_POLICY_MANAGE,
	/** Vehicles, tier, RFID tags and their lifecycle, limit rules. */
	FLEET_MANAGE,
	/** Station operators, stations, POS devices and agents. */
	NETWORK_MANAGE,
	/** Schedule fuel prices (PRD §6.1: set only by Operations). */
	PRICES_MANAGE,
	/** A company user reading their own company's data. */
	COMPANY_READ,
	/** A station manager reading their own operator's data. */
	STATION_READ;

	private static final Set<Permission> PLATFORM_OPERATIONS = EnumSet.of(USERS_READ, USERS_RESET_PASSWORD,
			USERS_UNLOCK, OPS_READ, COMPANIES_MANAGE, FLEET_MANAGE, NETWORK_MANAGE, PRICES_MANAGE);

	private static final Map<Role, Set<Permission>> BY_ROLE = Map.of(Role.SUPER_ADMIN,
			EnumSet.complementOf(EnumSet.of(COMPANY_READ, STATION_READ)), Role.OPS, PLATFORM_OPERATIONS,
			Role.FLEET_MANAGER, EnumSet.of(COMPANY_READ), Role.ACCOUNTANT, EnumSet.of(COMPANY_READ),
			Role.STATION_MANAGER, EnumSet.of(STATION_READ));

	public static Set<Permission> of(Role role) {
		return BY_ROLE.get(role);
	}

	/** The Spring Security authority name, e.g. {@code PERM_USERS_READ}. */
	public String authority() {
		return "PERM_" + name();
	}

}
