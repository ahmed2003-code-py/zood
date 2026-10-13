/**
 * Money and volume arithmetic. No floating point is used for financial values (PRD §2.3).
 *
 * - Money is stored as integer piastres (1 EGP = 100 piastres).
 * - Fuel volume is stored as integer milliliters (1 L = 1000 mL), i.e. liters with 3 decimals.
 * - Rounding is half-up (away from zero) to the nearest unit.
 */

export type Piastres = number;
export type Milliliters = number;

export const PIASTRES_PER_POUND = 100;
export const ML_PER_LITER = 1000;

function assertSafeInteger(value: number, name: string): void {
  if (!Number.isSafeInteger(value)) {
    throw new RangeError(`${name} must be a safe integer, got ${value}`);
  }
}

/** Integer division rounded half away from zero. */
export function divRoundHalfUp(numerator: number, denominator: number): number {
  assertSafeInteger(numerator, 'numerator');
  assertSafeInteger(denominator, 'denominator');
  if (denominator <= 0) throw new RangeError('denominator must be positive');
  const sign = numerator < 0 ? -1 : 1;
  const abs = Math.abs(numerator);
  return sign * Math.floor((2 * abs + denominator) / (2 * denominator));
}

/** Amount for a dispensed volume at a unit price (price per liter in piastres). */
export function amountForVolume(volumeMl: Milliliters, unitPricePiastres: Piastres): Piastres {
  return divRoundHalfUp(volumeMl * unitPricePiastres, ML_PER_LITER);
}

/** Volume that a given amount buys at a unit price. */
export function volumeForAmount(amountPiastres: Piastres, unitPricePiastres: Piastres): Milliliters {
  return divRoundHalfUp(amountPiastres * ML_PER_LITER, unitPricePiastres);
}

/**
 * Parses a user-entered decimal string ("42.5", "1,200.75") into integer minor units at the given
 * scale (2 for piastres, 3 for milliliters). Returns null for invalid input or too many decimals.
 */
export function parseDecimal(input: string, scale: number): number | null {
  const normalized = input.replace(/[,\s]/g, '');
  if (!/^\d+(\.\d*)?$/.test(normalized)) return null;
  const [whole = '0', fraction = ''] = normalized.split('.');
  if (fraction.length > scale) return null;
  const minor = Number(whole) * 10 ** scale + Number(fraction.padEnd(scale, '0') || '0');
  return Number.isSafeInteger(minor) ? minor : null;
}

export function poundsToPiastres(pounds: number): Piastres {
  assertSafeInteger(pounds, 'pounds');
  return pounds * PIASTRES_PER_POUND;
}

export function litersToMl(liters: number): Milliliters {
  assertSafeInteger(liters, 'liters');
  return liters * ML_PER_LITER;
}
