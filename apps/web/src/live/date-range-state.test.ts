import { describe, expect, it } from 'vitest';
import { isInvertedRange, usableRange } from './date-range-state';

describe('date ranges', () => {
  it('refuses a start after the end', () => {
    expect(isInvertedRange('2026-11-20', '2026-11-01')).toBe(true);
    expect(usableRange('2026-11-20', '2026-11-01')).toEqual({ from: '', to: '', valid: false });
  });

  it('accepts open ends and a single day', () => {
    expect(isInvertedRange('', '2026-11-01')).toBe(false);
    expect(isInvertedRange('2026-11-01', '')).toBe(false);
    expect(usableRange('2026-11-01', '2026-11-01')).toEqual({ from: '2026-11-01', to: '2026-11-01', valid: true });
  });
});
