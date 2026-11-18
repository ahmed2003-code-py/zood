/**
 * Quick periods for lists, statements, exports and the calendar (plan §7 B6): today, yesterday,
 * this and last week (weeks start on Saturday, as the fueling limits do), this and last month, the
 * last 7 and 30 days. Every period is a pair of Cairo calendar days, inclusive, as the API's
 * `from`/`to` day filters take them. "Last shift" comes from the server (an agent's session), so it
 * is not computed here.
 */

export type QuickPeriod = 'TODAY' | 'YESTERDAY' | 'THIS_WEEK' | 'LAST_WEEK' | 'THIS_MONTH' | 'LAST_MONTH' | 'LAST_7_DAYS' | 'LAST_30_DAYS';

export const QUICK_PERIODS: readonly QuickPeriod[] = [
  'TODAY',
  'YESTERDAY',
  'THIS_WEEK',
  'LAST_WEEK',
  'THIS_MONTH',
  'LAST_MONTH',
  'LAST_7_DAYS',
  'LAST_30_DAYS',
];

/** A range of Cairo days, inclusive, as YYYY-MM-DD. */
export interface DayRange {
  from: string;
  to: string;
}

const TIME_ZONE = 'Africa/Cairo';

/** The Cairo calendar day of an instant, as YYYY-MM-DD. */
export function cairoDay(at: Date): string {
  return at.toLocaleDateString('en-CA', { timeZone: TIME_ZONE });
}

/** Calendar arithmetic on a YYYY-MM-DD day, free of time zones and daylight saving. */
function addDays(day: string, days: number): string {
  const [year, month, date] = day.split('-').map(Number) as [number, number, number];
  return new Date(Date.UTC(year, month - 1, date + days)).toISOString().slice(0, 10);
}

function weekday(day: string): number {
  const [year, month, date] = day.split('-').map(Number) as [number, number, number];
  return new Date(Date.UTC(year, month - 1, date)).getUTCDay(); // 0 Sunday … 6 Saturday
}

/** The Saturday that starts the week containing `day`. */
function weekStart(day: string): string {
  return addDays(day, -((weekday(day) + 1) % 7));
}

export function periodRange(period: QuickPeriod, now: Date = new Date()): DayRange {
  const today = cairoDay(now);
  switch (period) {
    case 'TODAY':
      return { from: today, to: today };
    case 'YESTERDAY': {
      const yesterday = addDays(today, -1);
      return { from: yesterday, to: yesterday };
    }
    case 'THIS_WEEK':
      return { from: weekStart(today), to: today };
    case 'LAST_WEEK': {
      const start = addDays(weekStart(today), -7);
      return { from: start, to: addDays(start, 6) };
    }
    case 'THIS_MONTH':
      return { from: `${today.slice(0, 8)}01`, to: today };
    case 'LAST_MONTH': {
      const firstOfThis = `${today.slice(0, 8)}01`;
      const lastOfPrevious = addDays(firstOfThis, -1);
      return { from: `${lastOfPrevious.slice(0, 8)}01`, to: lastOfPrevious };
    }
    case 'LAST_7_DAYS':
      return { from: addDays(today, -6), to: today };
    case 'LAST_30_DAYS':
      return { from: addDays(today, -29), to: today };
  }
}
