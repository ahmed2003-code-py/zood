/**
 * Central, locale-aware formatting. Components never format money, volumes or dates themselves.
 *
 * Western digits (0-9) are used in both Arabic and English for operational readability.
 * Values are formatted from integer minor units, never through floating point.
 */
import { divRoundHalfUp, ML_PER_LITER, PIASTRES_PER_POUND, type Milliliters, type Piastres } from '@/domain/money';
import type { Locale } from '@/lib/locale';

export const DEFAULT_TIME_ZONE = 'Africa/Cairo';

const NUMBER_LOCALE: Record<Locale, string> = { ar: 'ar-EG-u-nu-latn', en: 'en-GB' };
const DATE_LOCALE: Record<Locale, string> = { ar: 'ar-EG-u-nu-latn', en: 'en-GB' };
const TIME_LOCALE: Record<Locale, string> = { ar: 'ar-EG-u-nu-latn', en: 'en-US' };

const CURRENCY_LABEL: Record<Locale, string> = { ar: 'ج.م', en: 'EGP' };
const LITER_LABEL: Record<Locale, string> = { ar: 'لتر', en: 'L' };

const NBSP = ' ';
// Left-to-right isolate: keeps a signed number ("-760.00") in order inside right-to-left text.
const LRI = '⁦';
const PDI = '⁩';

const integerFormatters = new Map<Locale, Intl.NumberFormat>();

function groupInteger(value: number, locale: Locale): string {
  let formatter = integerFormatters.get(locale);
  if (!formatter) {
    formatter = new Intl.NumberFormat(NUMBER_LOCALE[locale], { maximumFractionDigits: 0, useGrouping: true });
    integerFormatters.set(locale, formatter);
  }
  return formatter.format(value);
}

/** Formats an integer of minor units with a fixed number of decimals, e.g. 4250 (scale 2) -> "42.50". */
function formatScaled(minor: number, scale: number, fractionDigits: number, locale: Locale): string {
  const abs = Math.abs(minor);
  const rounded = divRoundHalfUp(abs, 10 ** (scale - fractionDigits));
  const unit = 10 ** fractionDigits;
  const whole = Math.floor(rounded / unit);
  const fraction = rounded % unit;
  const grouped = groupInteger(whole, locale);
  return fractionDigits > 0 ? `${grouped}.${String(fraction).padStart(fractionDigits, '0')}` : grouped;
}

function withSign(body: string, value: number, signDisplay: 'auto' | 'always'): string {
  if (value < 0) return `-${body}`;
  if (signDisplay === 'always' && value > 0) return `+${body}`;
  return body;
}

export function formatNumber(value: number, locale: Locale): string {
  return withSign(groupInteger(Math.abs(Math.trunc(value)), locale), Math.trunc(value), 'auto');
}

export interface MoneyFormatOptions {
  /** exact: 2 decimals (transactions, ledger). whole: rounded to pounds (KPIs). */
  precision?: 'exact' | 'whole';
  /** always: show "+" for positive values (e.g. ledger credits). */
  signDisplay?: 'auto' | 'always';
}

/** 345028000 -> "3,450,280.00 ج.م" (ar) | "EGP 3,450,280.00" (en). */
export function formatMoney(piastres: Piastres, locale: Locale, options: MoneyFormatOptions = {}): string {
  const { precision = 'exact', signDisplay = 'auto' } = options;
  const digits = precision === 'exact' ? 2 : 0;
  const signed = withSign(formatScaled(piastres, 2, digits, locale), piastres, signDisplay);
  const hasSign = piastres < 0 || (signDisplay === 'always' && piastres > 0);
  if (locale === 'ar') {
    const number = hasSign ? `${LRI}${signed}${PDI}` : signed;
    return `${number}${NBSP}${CURRENCY_LABEL.ar}`;
  }
  return `${CURRENCY_LABEL.en}${NBSP}${signed}`;
}

/** Money amount without the currency label, e.g. for chart axes. */
export function formatMoneyValue(piastres: Piastres, locale: Locale, precision: 'exact' | 'whole' = 'whole'): string {
  return withSign(formatScaled(piastres, 2, precision === 'exact' ? 2 : 0, locale), piastres, 'auto');
}

export function currencyLabel(locale: Locale): string {
  return CURRENCY_LABEL[locale];
}

/** 42500 mL -> "42.50 لتر" (ar) | "42.50 L" (en). */
export function formatVolume(volumeMl: Milliliters, locale: Locale, fractionDigits = 2): string {
  const body = withSign(formatScaled(volumeMl, 3, fractionDigits, locale), volumeMl, 'auto');
  return `${body}${NBSP}${LITER_LABEL[locale]}`;
}

export function volumeUnitLabel(locale: Locale): string {
  return LITER_LABEL[locale];
}

/** Whole pounds from piastres for chart scales (display only). */
export function piastresToWholePounds(piastres: Piastres): number {
  return divRoundHalfUp(piastres, PIASTRES_PER_POUND);
}

export function mlToWholeLiters(volumeMl: Milliliters): number {
  return divRoundHalfUp(volumeMl, ML_PER_LITER);
}

/* ---------------------------------- Dates --------------------------------- */

export interface DateFormatOptions {
  timeZone?: string;
  withYear?: boolean;
  /** long: "12 أكتوبر" / short month in English: "12 Oct". */
  month?: 'long' | 'short';
}

function toDate(value: string | Date): Date {
  return value instanceof Date ? value : new Date(value);
}

export function formatDate(value: string | Date, locale: Locale, options: DateFormatOptions = {}): string {
  const { timeZone = DEFAULT_TIME_ZONE, withYear = false } = options;
  const month = options.month ?? (locale === 'ar' ? 'long' : 'short');
  return new Intl.DateTimeFormat(DATE_LOCALE[locale], {
    day: 'numeric',
    month,
    ...(withYear ? { year: 'numeric' } : {}),
    timeZone,
  }).format(toDate(value));
}

export function formatTime(value: string | Date, locale: Locale, timeZone = DEFAULT_TIME_ZONE): string {
  return new Intl.DateTimeFormat(TIME_LOCALE[locale], {
    hour: 'numeric',
    minute: '2-digit',
    hour12: true,
    timeZone,
  }).format(toDate(value));
}

/** "12 أكتوبر • 12:42 م" | "12 Oct • 12:42 PM" */
export function formatDateTime(value: string | Date, locale: Locale, options: DateFormatOptions = {}): string {
  return `${formatDate(value, locale, options)} • ${formatTime(value, locale, options.timeZone)}`;
}

/** "قبل ساعتين" | "2 hours ago". Falls back to a date after 7 days. */
export function formatRelative(value: string | Date, locale: Locale, now: Date = new Date()): string {
  const diffSeconds = Math.round((toDate(value).getTime() - now.getTime()) / 1000);
  const abs = Math.abs(diffSeconds);
  const rtf = new Intl.RelativeTimeFormat(NUMBER_LOCALE[locale], { numeric: 'auto' });
  if (abs < 60) return rtf.format(0, 'minute');
  if (abs < 3600) return rtf.format(Math.round(diffSeconds / 60), 'minute');
  if (abs < 86400) return rtf.format(Math.round(diffSeconds / 3600), 'hour');
  if (abs < 7 * 86400) return rtf.format(Math.round(diffSeconds / 86400), 'day');
  return formatDate(value, locale);
}

/** Hour of day (0–23) at an instant in a time zone, e.g. for a time-of-day greeting. */
export function localHour(value: Date, timeZone = DEFAULT_TIME_ZONE): number {
  const hour = new Intl.DateTimeFormat('en-US', { hour: 'numeric', hourCycle: 'h23', timeZone })
    .formatToParts(value)
    .find((part) => part.type === 'hour')?.value;
  return Number(hour ?? 0);
}

/** "12/10/2026" (day/month/year, Western digits) for compact receipts, in both languages. */
export function formatNumericDate(value: string | Date, timeZone = DEFAULT_TIME_ZONE): string {
  const parts = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: '2-digit', year: 'numeric', timeZone }).formatToParts(toDate(value));
  const get = (type: string) => parts.find((part) => part.type === type)?.value ?? '';
  return `${get('day')}/${get('month')}/${get('year')}`;
}

/** "2:59" (hours:minutes) until an instant, never negative. */
export function remainingHoursMinutes(until: string | Date, now: Date): { hours: string; minutes: string } {
  const totalMinutes = Math.max(0, Math.ceil((toDate(until).getTime() - now.getTime()) / 60_000));
  return { hours: String(Math.floor(totalMinutes / 60)), minutes: String(totalMinutes % 60).padStart(2, '0') };
}
