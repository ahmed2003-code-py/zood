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
	AUDIT_READ;

	private static final Map<Role, Set<Permission>> BY_ROLE = Map.of(Role.SUPER_ADMIN, EnumSet.allOf(Permission.class),
			Role.OPS, EnumSet.of(USERS_READ, USERS_RESET_PASSWORD, USERS_UNLOCK), Role.FLEET_MANAGER,
			EnumSet.noneOf(Permission.class), Role.ACCOUNTANT, EnumSet.noneOf(Permission.class),
			Role.STATION_MANAGER, EnumSet.noneOf(Permission.class));

	public static Set<Permission> of(Role role) {
		return BY_ROLE.get(role);
	}

	/** The Spring Security authority name, e.g. {@code PERM_USERS_READ}. */
	public String authority() {
		return "PERM_" + name();
	}

}
