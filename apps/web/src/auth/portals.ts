import type { Role } from './types';

export type PortalId = 'company' | 'station' | 'ops';

/**
 * Who may open each portal. The UI uses this only to send people to the right place; every API
 * endpoint enforces its own permission, so hiding a portal is never the protection.
 */
export const PORTAL_ROLES: Record<PortalId, readonly Role[]> = {
  company: ['FLEET_MANAGER', 'ACCOUNTANT'],
  station: ['STATION_MANAGER'],
  ops: ['OPS', 'OPS_MANAGER', 'SUPER_ADMIN'],
};

/** Where each role lands after signing in. */
export const HOME_BY_ROLE: Record<Role, string> = {
  SUPER_ADMIN: '/ops',
  OPS: '/ops',
  OPS_MANAGER: '/ops',
  FLEET_MANAGER: '/company',
  ACCOUNTANT: '/company',
  STATION_MANAGER: '/station',
};

/**
 * The one sign-in page, for every role (owner, 2026-11-20; ADR 0008 amendment): the role the API
 * returns decides which portal opens.
 */
export const LOGIN_PATH = '/login';

/** The Super Admin's former sign-in page. Old bookmarks land on the one sign-in page instead. */
export const LEGACY_ADMIN_LOGIN_PATH = '/admin/login';

/** Where a bookmark of the former admin sign-in goes: the one sign-in page, keeping `?next=`. */
export function legacyAdminLoginTarget(search: string): string {
  return `${LOGIN_PATH}${search}`;
}

/**
 * A `next` value is followed only when it is a path inside this app. Anything else (another site,
 * a protocol-relative URL, a backslash trick) is ignored, so a crafted sign-in link cannot send a
 * user elsewhere after they enter their password.
 */
export function safeNext(next: string | null | undefined): string | undefined {
  if (!next || !next.startsWith('/') || next.startsWith('//') || next.includes('\\')) return undefined;
  // Browsers drop tabs and line breaks from URLs, so `/\t/evil.com` would become `//evil.com`.
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f]/.test(next)) return undefined;
  // The last word: the path must resolve to this origin.
  if (new URL(next, 'https://zawed.invalid').origin !== 'https://zawed.invalid') return undefined;
  if (next.startsWith(LOGIN_PATH) || next.startsWith(LEGACY_ADMIN_LOGIN_PATH)) return undefined;
  return next;
}
