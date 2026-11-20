import { describe, expect, it } from 'vitest';
import { HOME_BY_ROLE, legacyAdminLoginTarget, LOGIN_PATH, safeNext } from './portals';

describe('safeNext', () => {
  it('follows a path inside the app', () => {
    expect(safeNext('/company/vehicles?tab=all')).toBe('/company/vehicles?tab=all');
  });

  it('ignores anything that could leave the app', () => {
    for (const value of ['https://evil.example', '//evil.example', '/\\evil.example', 'evil', '', null, undefined]) {
      expect(safeNext(value)).toBeUndefined();
    }
  });

  it('does not loop back to a sign-in page', () => {
    expect(safeNext('/login')).toBeUndefined();
    expect(safeNext('/admin/login?next=/ops')).toBeUndefined();
  });
});

describe('one sign-in page for every role', () => {
  it('sends a bookmark of the former admin sign-in to /login, keeping next', () => {
    expect(LOGIN_PATH).toBe('/login');
    expect(legacyAdminLoginTarget('')).toBe('/login');
    expect(legacyAdminLoginTarget('?next=%2Fops%2Fcompanies')).toBe('/login?next=%2Fops%2Fcompanies');
  });

  it('opens the portal of the role the API returns', () => {
    expect(HOME_BY_ROLE.SUPER_ADMIN).toBe('/ops');
    expect(HOME_BY_ROLE.OPS).toBe('/ops');
    expect(HOME_BY_ROLE.OPS_MANAGER).toBe('/ops');
    expect(HOME_BY_ROLE.FLEET_MANAGER).toBe('/company');
    expect(HOME_BY_ROLE.ACCOUNTANT).toBe('/company');
    expect(HOME_BY_ROLE.STATION_MANAGER).toBe('/station');
  });
});
