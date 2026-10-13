/**
 * Calendar helpers evaluated in a station's time zone (PRD §2.3: store UTC, evaluate business
 * rules in the station's configured time zone).
 */
import type { LimitPeriod } from './types';

const partsFormatters = new Map<string, Intl.DateTimeFormat>();

function localParts(at: Date, timeZone: string): { year: number; month: number; day: number } {
  let formatter = partsFormatters.get(timeZone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' });
    partsFormatters.set(timeZone, formatter);
  }
  const parts = formatter.formatToParts(at);
  const get = (type: string) => Number(parts.find((part) => part.type === type)?.value);
  return { year: get('year'), month: get('month'), day: get('day') };
}

/** Day number since epoch of the local calendar date (time-of-day ignored). */
function localDayNumber(at: Date, timeZone: string): number {
  const { year, month, day } = localParts(at, timeZone);
  return Math.floor(Date.UTC(year, month - 1, day) / 86_400_000);
}

/**
 * Key identifying the period an instant belongs to. Weeks start on Saturday (Egyptian work week).
 * Two instants are in the same period exactly when their keys are equal.
 */
export function periodKey(at: Date, period: LimitPeriod, timeZone: string): string {
  if (period === 'month') {
    const { year, month } = localParts(at, timeZone);
    return `${year}-${month}`;
  }
  const dayNumber = localDayNumber(at, timeZone);
  if (period === 'day') return `d${dayNumber}`;
  // 1970-01-01 was a Thursday; Saturday-based week index.
  return `w${Math.floor((dayNumber + 5) / 7)}`;
}

export function isSamePeriod(a: Date, b: Date, period: LimitPeriod, timeZone: string): boolean {
  return periodKey(a, period, timeZone) === periodKey(b, period, timeZone);
}

const offsetFormatters = new Map<string, Intl.DateTimeFormat>();

/** Offset of a time zone from UTC at an instant, in minutes (e.g. +180 for Cairo in summer). */
function timeZoneOffsetMinutes(at: Date, timeZone: string): number {
  let formatter = offsetFormatters.get(timeZone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('en-US', {
      timeZone,
      hourCycle: 'h23',
      year: 'numeric',
      month: 'numeric',
      day: 'numeric',
      hour: 'numeric',
      minute: 'numeric',
    });
    offsetFormatters.set(timeZone, formatter);
  }
  const parts = formatter.formatToParts(at);
  const get = (type: string) => Number(parts.find((part) => part.type === type)?.value);
  const asUtc = Date.UTC(get('year'), get('month') - 1, get('day'), get('hour'), get('minute'));
  return Math.round((asUtc - Math.floor(at.getTime() / 60_000) * 60_000) / 60_000);
}

/**
 * The instant at a local wall-clock time, `daysAgo` local days before `reference`, in a time zone.
 * Used to generate realistic station-local demo times.
 */
export function zonedInstant(reference: Date, daysAgo: number, hour: number, minute: number, timeZone: string): Date {
  const { year, month, day } = localParts(reference, timeZone);
  const guess = Date.UTC(year, month - 1, day - daysAgo, hour, minute);
  const offset = timeZoneOffsetMinutes(new Date(guess), timeZone);
  return new Date(guess - offset * 60_000);
}
