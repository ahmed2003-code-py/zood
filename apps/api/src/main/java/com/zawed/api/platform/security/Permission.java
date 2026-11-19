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
	STATION_READ,

	// ---------------------------------------------------------------------------- B3 ledger and wallet

	/**
	 * Ask Operations to credit a bank transfer, and cancel such a request while pending (PRD §4.6:
	 * the company finance team notifies Operations; §6.2: a wallet top-up is a Fleet Panel ticket).
	 * Both company roles: it moves no money and is not a configuration action.
	 */
	TOPUP_REQUEST,
	/**
	 * Confirm or reject a top-up request, and credit a wallet directly against a bank reference
	 * (PRD §3: Operations "recharge corporate wallets"; §6.3 "Credit Wallet action").
	 */
	WALLET_CREDIT,
	/** Start a wallet reconciliation run now, outside the nightly schedule. Super Admin only. */
	RECONCILIATION_RUN,
	/**
	 * Approve money entering a wallet (V12, ADR 0021): a verified bank transfer, or a wallet
	 * adjustment. Ops Manager and Super Admin; never the person who verified or asked (service and
	 * database rule). WALLET_CREDIT is now the maker's half: verify, record, ask.
	 */
	TOPUP_APPROVE,

	// ------------------------------------------------------------------------------- B4 fueling

	/** Acknowledge a hard alert (PRD §10): Operations has seen it and acted. */
	ALERTS_MANAGE,

	// ------------------------------------------------------------------------ B6 station finance

	/*
	 * Dual control (plan §6 "internal admin fraud", roadmap delta A1, A3, A4). Every pair below is
	 * "ask" and "decide". Super Admin and Operations hold both halves of each pair, because a small
	 * team must be able to cover for each other; what keeps the control is that nobody decides their
	 * own request. That rule is not a permission: the service refuses it and the database refuses it
	 * (V9), whoever the two people are.
	 */

	/** Record a payout to a station, and withdraw one that still waits for approval. */
	PAYOUTS_MANAGE,
	/** Approve a payout above the threshold. Never the person who recorded it. */
	PAYOUTS_APPROVE,
	/** Request a change of a station's bank account, with the bank letter's reference. */
	BANK_DETAILS_MANAGE,
	/** Approve or reject a bank account change. Approval is never by the person who requested it. */
	BANK_DETAILS_APPROVE,
	/** Request the reversal of a completed fueling (PRD §6.4). */
	REVERSALS_REQUEST,
	/** Approve or reject a reversal request. Approval is never by the person who requested it. */
	REVERSALS_APPROVE,
	/** Release a payable that offline sync held for Operations (B5 contract §4). */
	PAYABLE_HOLD_RELEASE,

	// ---------------------------------------------------------------------------------- B6 tickets

	/**
	 * Open a support ticket and write on one's own tickets (roadmap delta A5): a company or a station
	 * asks Operations for something. A ticket is a request, not a change to master data.
	 */
	TICKETS_OPEN,
	/** Answer tickets, write internal notes and move a ticket's status. */
	TICKETS_MANAGE;

	private static final Set<Permission> PLATFORM_OPERATIONS = EnumSet.of(USERS_READ, USERS_RESET_PASSWORD,
			USERS_UNLOCK, OPS_READ, COMPANIES_MANAGE, FLEET_MANAGE, NETWORK_MANAGE, PRICES_MANAGE, WALLET_CREDIT, ALERTS_MANAGE,
			PAYOUTS_MANAGE, PAYOUTS_APPROVE, BANK_DETAILS_MANAGE, BANK_DETAILS_APPROVE, REVERSALS_REQUEST,
			REVERSALS_APPROVE, PAYABLE_HOLD_RELEASE, TICKETS_MANAGE);

	private static final Set<Permission> OPS_MANAGER = with(PLATFORM_OPERATIONS, TOPUP_APPROVE);

	private static final Set<Permission> COMPANY_USERS = EnumSet.of(COMPANY_READ, TOPUP_REQUEST, TICKETS_OPEN);

	private static final Map<Role, Set<Permission>> BY_ROLE = Map.of(Role.SUPER_ADMIN,
			EnumSet.complementOf(EnumSet.of(COMPANY_READ, STATION_READ, TOPUP_REQUEST, TICKETS_OPEN)), Role.OPS_MANAGER,
			OPS_MANAGER, Role.OPS, PLATFORM_OPERATIONS, Role.FLEET_MANAGER, COMPANY_USERS, Role.ACCOUNTANT, COMPANY_USERS, Role.STATION_MANAGER,
			EnumSet.of(STATION_READ, TICKETS_OPEN));

	private static Set<Permission> with(Set<Permission> base, Permission extra) {
		EnumSet<Permission> set = EnumSet.copyOf(base);
		set.add(extra);
		return set;
	}

	public static Set<Permission> of(Role role) {
		return BY_ROLE.get(role);
	}

	/** The Spring Security authority name, e.g. {@code PERM_USERS_READ}. */
	public String authority() {
		return "PERM_" + name();
	}

}
