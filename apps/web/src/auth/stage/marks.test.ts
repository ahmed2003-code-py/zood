import { describe, expect, it } from 'vitest';
import { LOGO } from '@/design-system/brand/logo-geometry';
import { DROP_HIGHLIGHT, DROP_PATH, SPEED_LINE_WIDTHS } from './marks';
import { SUCCESS_MS } from './state';

describe('sign-in stage marks', () => {
  it('match the generated logo geometry', () => {
    expect(DROP_PATH).toBe(LOGO.dropPath);
    expect(DROP_HIGHLIGHT).toBe(LOGO.dropHighlight);
    expect([...SPEED_LINE_WIDTHS]).toEqual(LOGO.speedLines.map((line) => line.w));
  });
});

describe('the signed-in moment', () => {
  it('never holds the visitor for more than a second (owner, 2026-11-21)', () => {
    expect(SUCCESS_MS).toBeGreaterThan(0);
    expect(SUCCESS_MS).toBeLessThanOrEqual(1_000);
  });
});
