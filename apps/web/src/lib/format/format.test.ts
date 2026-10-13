import { describe, expect, it } from 'vitest';
import { formatDate, formatDateTime, formatMoney, formatNumber, formatRelative, formatTime, formatVolume } from './index';

const NBSP = ' ';
const LRI = '⁦';
const PDI = '⁩';

describe('formatMoney', () => {
  it('formats Arabic with Western digits and the currency after the number', () => {
    expect(formatMoney(345_028_000, 'ar', { precision: 'whole' })).toBe(`3,450,280${NBSP}ج.م`);
    expect(formatMoney(76_000, 'ar')).toBe(`760.00${NBSP}ج.م`);
  });

  it('formats English with the currency code before the number', () => {
    expect(formatMoney(345_028_000, 'en', { precision: 'whole' })).toBe(`EGP${NBSP}3,450,280`);
    expect(formatMoney(76_000, 'en')).toBe(`EGP${NBSP}760.00`);
  });

  it('isolates signed numbers in Arabic so the sign stays in place in RTL text', () => {
    expect(formatMoney(-76_000, 'ar')).toBe(`${LRI}-760.00${PDI}${NBSP}ج.م`);
    expect(formatMoney(5_000_000, 'ar', { signDisplay: 'always' })).toBe(`${LRI}+50,000.00${PDI}${NBSP}ج.م`);
    expect(formatMoney(-76_000, 'en')).toBe(`EGP${NBSP}-760.00`);
  });

  it('rounds to whole pounds half-up without floating point', () => {
    expect(formatMoney(149, 'en', { precision: 'whole' })).toBe(`EGP${NBSP}1`);
    expect(formatMoney(150, 'en', { precision: 'whole' })).toBe(`EGP${NBSP}2`);
  });
});

describe('formatVolume', () => {
  it('formats liters with two decimals', () => {
    expect(formatVolume(42_500, 'ar')).toBe(`42.50${NBSP}لتر`);
    expect(formatVolume(42_500, 'en')).toBe(`42.50${NBSP}L`);
    expect(formatVolume(42_345, 'en')).toBe(`42.35${NBSP}L`);
  });
});

describe('formatNumber', () => {
  it('groups digits in both locales', () => {
    expect(formatNumber(1_240_000, 'ar')).toBe('1,240,000');
    expect(formatNumber(1_240_000, 'en')).toBe('1,240,000');
  });
});

describe('dates', () => {
  // 09:42 UTC = 12:42 in Cairo (UTC+3 in October 2026).
  const at = '2026-10-12T09:42:00Z';

  it('formats Arabic dates and times with Western digits', () => {
    expect(formatDate(at, 'ar')).toBe('12 أكتوبر');
    expect(formatTime(at, 'ar')).toBe('12:42 م');
    expect(formatDateTime(at, 'ar')).toBe('12 أكتوبر • 12:42 م');
  });

  it('formats English dates and times', () => {
    expect(formatDateTime(at, 'en')).toBe('12 Oct • 12:42 PM');
  });

  it('formats relative time', () => {
    const now = new Date('2026-10-12T11:42:00Z');
    expect(formatRelative(at, 'en', now)).toBe('2 hours ago');
    expect(formatRelative(at, 'ar', now)).toBe('قبل ساعتين');
  });
});
