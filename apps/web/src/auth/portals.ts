import type { Door, Role } from './types';

export type PortalId = 'company' | 'station' | 'ops';

/**
 * Who may open each portal. The UI uses this only to send people to the right place; every API
 * endpoint enforces its own permission, so hiding a portal is never the protection.
 */
export const PORTAL_ROLES: Record<PortalId, readonly Role[]> = {
  company: ['FLEET_MANAGER', 'ACCOUNTANT'],
  station: ['STATION_MANAGER'],
  ops: ['OPS', 'SUPER_ADMIN'],
};

/** Where each role lands after signing in. */
export const HOME_BY_ROLE: Record<Role, string> = {
  SUPER_ADMIN: '/ops',
  OPS: '/ops',
  FLEET_MANAGER: '/company',
  ACCOUNTANT: '/company',
  STATION_MANAGER: '/station',
};

export function loginPath(door: Door): string {
  return door === 'admin' ? '/admin/login' : '/login';
}

/**
 * A `next` value is followed only when it is a path inside this app. Anything else (another site,
 * a protocol-relative URL, a backslash trick) is ignored, so a crafted sign-in link cannot send a
 * user elsewhere after they enter their password.
 */
export function safeNext(next: string | null | undefined): string | undefined {
  if (!next || !next.startsWith('/') || next.startsWith('//') || next.includes('\\')) return undefined;
  if (next.startsWith('/login') || next.startsWith('/admin/login')) return undefined;
  return next;
}
