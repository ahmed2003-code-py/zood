import { describe, expect, it } from 'vitest';
import { amountForVolume, divRoundHalfUp, parseDecimal, volumeForAmount } from './money';

describe('divRoundHalfUp', () => {
  it('rounds half away from zero', () => {
    expect(divRoundHalfUp(5, 10)).toBe(1);
    expect(divRoundHalfUp(4, 10)).toBe(0);
    expect(divRoundHalfUp(-5, 10)).toBe(-1);
    expect(divRoundHalfUp(15, 10)).toBe(2);
  });

  it('rejects non-integers', () => {
    expect(() => divRoundHalfUp(0.1, 10)).toThrow(RangeError);
  });
});

describe('fuel amounts', () => {
  it('computes 40 L of gasoline 92 at 19.00 EGP/L as 760.00 EGP', () => {
    expect(amountForVolume(40_000, 1900)).toBe(76_000);
  });

  it('rounds partial piastres half-up', () => {
    // 42.345 L x 17.50 = 741.0375 EGP -> 741.04
    expect(amountForVolume(42_345, 1750)).toBe(74_104);
  });

  it('converts an amount back to volume', () => {
    expect(volumeForAmount(76_000, 1900)).toBe(40_000);
    // 500 EGP at 19.00 = 26.315789... L -> 26.316 L
    expect(volumeForAmount(50_000, 1900)).toBe(26_316);
  });
});

describe('parseDecimal', () => {
  it('parses user input into minor units', () => {
    expect(parseDecimal('42.5', 3)).toBe(42_500);
    expect(parseDecimal('1,200.75', 2)).toBe(120_075);
    expect(parseDecimal('40', 3)).toBe(40_000);
    expect(parseDecimal('7.', 2)).toBe(700);
  });

  it('rejects invalid input and excess decimals', () => {
    expect(parseDecimal('abc', 2)).toBeNull();
    expect(parseDecimal('1.234', 2)).toBeNull();
    expect(parseDecimal('-5', 2)).toBeNull();
  });

  // The same cases are in apps/api MoneyMathTests: the POS and the server must agree.
  it('rejects what the server rejects too', () => {
    expect(parseDecimal('', 2)).toBeNull();
    expect(parseDecimal('.5', 2)).toBeNull();
    expect(parseDecimal('٤٢', 2)).toBeNull(); // Arabic-Indic digits are not accepted
    expect(parseDecimal('90071992547409.92', 2)).toBeNull(); // beyond the safe range
  });

  it('treats spaces exactly as the server does', () => {
    expect(parseDecimal('1 200.5', 2)).toBe(120_050); // no-break space
    expect(parseDecimal('1 200', 2)).toBe(120_000); // narrow no-break space
    expect(parseDecimal('42﻿', 2)).toBe(4_200); // byte-order mark
    expect(parseDecimal('4\u00852', 2)).toBeNull(); // NEL is not white space here
  });
});
