/**
 * What people type into the master-data forms, turned into what the API stores: integer piastres
 * and integer millilitres (never floating point), trimmed text, and Western digits whatever the
 * keyboard produced.
 */
import { ML_PER_LITER, parseDecimal, PIASTRES_PER_POUND, type Milliliters, type Piastres } from '@/domain/money';

/** Eastern Arabic and Persian digits to Western, and the Arabic decimal and thousands separators. */
export function normalizeDigits(input: string): string {
  return input
    .replace(/[٠-٩]/g, (digit) => String(digit.charCodeAt(0) - 0x0660))
    .replace(/[۰-۹]/g, (digit) => String(digit.charCodeAt(0) - 0x06f0))
    .replace(/٫/g, '.') // Arabic decimal separator
    .replace(/[٬،]/g, ','); // Arabic thousands separator, Arabic comma
}

/** "1,250.5" or "١٢٥٠٫٥" pounds → 125050 piastres. Null when it is not an amount. */
export function parsePounds(input: string): Piastres | null {
  return parseDecimal(normalizeDigits(input.trim()), 2);
}

/** "42.5" litres → 42500 millilitres. Null when it is not a volume. */
export function parseLitres(input: string): Milliliters | null {
  return parseDecimal(normalizeDigits(input.trim()), 3);
}

/** A whole number (a year, minutes), or null. */
export function parseWhole(input: string): number | null {
  const value = normalizeDigits(input.trim());
  return /^\d+$/.test(value) && Number.isSafeInteger(Number(value)) ? Number(value) : null;
}

/** Piastres back to the pounds a form shows ("1250.50"), without floating point. */
export function piastresToInput(value: Piastres | undefined | null): string {
  if (value === undefined || value === null) return '';
  const pounds = Math.trunc(value / PIASTRES_PER_POUND);
  const rest = value % PIASTRES_PER_POUND;
  return rest === 0 ? String(pounds) : `${pounds}.${String(rest).padStart(2, '0')}`;
}

/** Millilitres back to the litres a form shows ("42.5"). */
export function mlToInput(value: Milliliters | undefined | null): string {
  if (value === undefined || value === null) return '';
  const litres = Math.trunc(value / ML_PER_LITER);
  const rest = value % ML_PER_LITER;
  return rest === 0 ? String(litres) : `${litres}.${String(rest).padStart(3, '0').replace(/0+$/, '')}`;
}

/** Trimmed text, or undefined when empty, so optional fields are not sent as "". */
export function optional(input: string): string | undefined {
  const value = input.trim();
  return value === '' ? undefined : value;
}
