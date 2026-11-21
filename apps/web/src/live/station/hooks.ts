/** Station-portal hooks shared by its pages. */
import { useLocale } from '@/i18n/useLocale';
import { cairoDay } from '@/lib/periods';
import type { Station } from '../api';
import { rangeProblem } from './station-model';

export interface DayRangeValue {
  from: string;
  to: string;
}

/** The range the API may be asked for: empty while the chosen days are not valid. */
export function useValidRange(range: DayRangeValue) {
  const today = cairoDay(new Date());
  const problem = rangeProblem(range.from, range.to, today);
  return { problem, valid: problem ? { from: '', to: '' } : range, today };
}

/** A station's name in the reader's language. */
export function useStationName(stations: readonly Station[] | undefined) {
  const { locale } = useLocale();
  return (id: string | undefined) => {
    const station = stations?.find((candidate) => candidate.id === id);
    return station ? (locale === 'ar' ? station.nameAr : station.nameEn) : undefined;
  };
}
