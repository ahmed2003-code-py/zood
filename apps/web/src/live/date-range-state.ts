/**
 * The state behind a period filter (see date-range.tsx): a quick period or two Cairo calendar days.
 * A range that ends before it starts is never sent to the API.
 */
import { useState } from 'react';
import { periodRange, type DayRange, type QuickPeriod } from '@/lib/periods';

export type PeriodChoice = 'CUSTOM' | QuickPeriod;

/** True when both ends are set and the start is after the end (YYYY-MM-DD compares as text). */
export function isInvertedRange(from: string, to: string): boolean {
  return Boolean(from) && Boolean(to) && from > to;
}

/** The range to query: undefined ends stay open; an inverted range queries nothing. */
export function usableRange(from: string, to: string): { from: string; to: string; valid: boolean } {
  const valid = !isInvertedRange(from, to);
  return valid ? { from, to, valid } : { from: '', to: '', valid };
}

export interface DateRangeState {
  period: PeriodChoice;
  from: string;
  to: string;
  setPeriod: (period: PeriodChoice) => void;
  setFrom: (from: string) => void;
  setTo: (to: string) => void;
}

/** State for a period filter, starting from a quick period or from given days. */
export function useDateRange(initial: PeriodChoice | DayRange = 'CUSTOM'): DateRangeState {
  const start = typeof initial === 'string' ? (initial === 'CUSTOM' ? { from: '', to: '' } : periodRange(initial)) : initial;
  const [period, setPeriodState] = useState<PeriodChoice>(typeof initial === 'string' ? initial : 'CUSTOM');
  const [from, setFromState] = useState(start.from);
  const [to, setToState] = useState(start.to);
  return {
    period,
    from,
    to,
    setPeriod: (next) => {
      setPeriodState(next);
      if (next !== 'CUSTOM') {
        const range = periodRange(next);
        setFromState(range.from);
        setToState(range.to);
      }
    },
    setFrom: (value) => {
      setPeriodState('CUSTOM');
      setFromState(value);
    },
    setTo: (value) => {
      setPeriodState('CUSTOM');
      setToState(value);
    },
  };
}
