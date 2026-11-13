import { describe, expect, it } from 'vitest';
import { safeNext } from './portals';

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
