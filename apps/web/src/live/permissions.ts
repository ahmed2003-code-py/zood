/**
 * What each role may do, as the API decides it (`platform/security/Permission.java`). The API is
 * the authority and refuses anything else; the web uses this only to hide what a role cannot do,
 * so nobody is shown a button or a menu item that would answer 403.
 */
import { useSession } from '@/auth/session-store';
import type { Role } from '@/auth/types';

export type Capability =
  /** Read the audit log (AUDIT_READ). */
  | 'auditRead'
  /** Approve money entering a wallet: a verified top-up or a wallet adjustment (TOPUP_APPROVE). */
  | 'topUpApprove'
  /** Verify and record transfers, ask for adjustments (WALLET_CREDIT). */
  | 'walletCredit'
  /** Create users and change their status (USERS_CREATE, USERS_STATUS). */
  | 'usersManage'
  /** Change a company's cooldown and low-balance threshold (COMPANY_POLICY_MANAGE). */
  | 'companyPolicy'
  /** Start a reconciliation run now (RECONCILIATION_RUN). */
  | 'reconciliationRun'
  /** See and manage platform roles in the user list; Ops sees scoped roles only. */
  | 'platformUsers';

const SUPER_ADMIN: readonly Capability[] = [
  'auditRead',
  'topUpApprove',
  'walletCredit',
  'usersManage',
  'companyPolicy',
  'reconciliationRun',
  'platformUsers',
];

const BY_ROLE: Record<Role, readonly Capability[]> = {
  SUPER_ADMIN,
  OPS_MANAGER: ['topUpApprove', 'walletCredit'],
  OPS: ['walletCredit'],
  FLEET_MANAGER: [],
  ACCOUNTANT: [],
  STATION_MANAGER: [],
};

/** The roles an Ops user (not a Super Admin) can see and filter by in the user list. */
export const SCOPED_ROLES: readonly Role[] = ['FLEET_MANAGER', 'ACCOUNTANT', 'STATION_MANAGER'];

export function can(role: Role | undefined, capability: Capability): boolean {
  return role !== undefined && BY_ROLE[role].includes(capability);
}

/** The signed-in user's role and a check against it. */
export function useCan(): { role: Role | undefined; userId: string | undefined; can: (capability: Capability) => boolean } {
  const user = useSession((state) => state.user);
  return { role: user?.role, userId: user?.id, can: (capability) => can(user?.role, capability) };
}
