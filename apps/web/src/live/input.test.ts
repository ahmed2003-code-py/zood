import { describe, expect, it } from 'vitest';
import { mlToInput, normalizeDigits, optional, parseLitres, parsePounds, parseWhole, piastresToInput } from './input';

describe('form input', () => {
  it('reads Eastern Arabic and Persian digits and the Arabic separators', () => {
    expect(normalizeDigits('٤٥٨٢')).toBe('4582');
    expect(normalizeDigits('۱۲۳')).toBe('123');
    expect(parsePounds('١٬٢٥٠٫٥')).toBe(125_050);
  });

  it('turns pounds into integer piastres without floating point', () => {
    expect(parsePounds('1,250.75')).toBe(125_075);
    expect(parsePounds('0.1')).toBe(10);
    expect(parsePounds(' 19 ')).toBe(1_900);
    expect(parsePounds('1.234')).toBeNull();
    expect(parsePounds('-5')).toBeNull();
    expect(parsePounds('abc')).toBeNull();
  });

  it('turns litres into integer millilitres', () => {
    expect(parseLitres('42.5')).toBe(42_500);
    expect(parseLitres('٥٠')).toBe(50_000);
    expect(parseLitres('1.2345')).toBeNull();
  });

  it('reads whole numbers only', () => {
    expect(parseWhole('٢٠٢٤')).toBe(2024);
    expect(parseWhole('20.5')).toBeNull();
    expect(parseWhole('')).toBeNull();
  });

  it('shows stored values back in a form', () => {
    expect(piastresToInput(125_050)).toBe('1250.50');
    expect(piastresToInput(1_900)).toBe('19');
    expect(mlToInput(42_500)).toBe('42.5');
    expect(mlToInput(50_000)).toBe('50');
    expect(mlToInput(undefined)).toBe('');
  });

  it('sends empty optional text as nothing', () => {
    expect(optional('   ')).toBeUndefined();
    expect(optional(' x ')).toBe('x');
  });
});
