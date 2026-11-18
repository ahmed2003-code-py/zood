import { describe, expect, it } from 'vitest';
import { cairoDay, periodRange } from './periods';

// Sunday 15 November 2026, 10:00 in Cairo.
const NOW = new Date('2026-11-15T08:00:00Z');

describe('periodRange', () => {
  it('names days in Cairo, not UTC', () => {
    expect(cairoDay(new Date('2026-11-14T22:30:00Z'))).toBe('2026-11-15');
  });

  it('gives today and yesterday', () => {
    expect(periodRange('TODAY', NOW)).toEqual({ from: '2026-11-15', to: '2026-11-15' });
    expect(periodRange('YESTERDAY', NOW)).toEqual({ from: '2026-11-14', to: '2026-11-14' });
  });

  it('starts weeks on Saturday', () => {
    expect(periodRange('THIS_WEEK', NOW)).toEqual({ from: '2026-11-14', to: '2026-11-15' });
    expect(periodRange('LAST_WEEK', NOW)).toEqual({ from: '2026-11-07', to: '2026-11-13' });
    // On a Saturday the week is that day alone so far.
    expect(periodRange('THIS_WEEK', new Date('2026-11-14T08:00:00Z'))).toEqual({ from: '2026-11-14', to: '2026-11-14' });
  });

  it('gives this and last month, across a year end', () => {
    expect(periodRange('THIS_MONTH', NOW)).toEqual({ from: '2026-11-01', to: '2026-11-15' });
    expect(periodRange('LAST_MONTH', NOW)).toEqual({ from: '2026-10-01', to: '2026-10-31' });
    expect(periodRange('LAST_MONTH', new Date('2027-01-10T08:00:00Z'))).toEqual({ from: '2026-12-01', to: '2026-12-31' });
  });

  it('counts rolling ranges inclusive of today', () => {
    expect(periodRange('LAST_7_DAYS', NOW)).toEqual({ from: '2026-11-09', to: '2026-11-15' });
    expect(periodRange('LAST_30_DAYS', NOW)).toEqual({ from: '2026-10-17', to: '2026-11-15' });
  });
});
