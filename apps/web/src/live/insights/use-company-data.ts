/**
 * The company's own data for the dashboards, budgets, limits and reports, read through the API's
 * company endpoints only (the API scopes every one of them to the caller's company).
 */
import { cairoDay, periodRange, type DayRange } from '@/lib/periods';
import { companyApi, type Fueling, type Vehicle } from '../api';
import { cairoDayStart } from '../input';
import { useResource } from '../use-resource';
import { collectPages, type Collected } from './model';

/** At most this many fuelings feed a breakdown; a pilot fleet's month fits well inside it. */
export const FUELING_CAP = 2000;
export const VEHICLE_CAP = 1000;

/** Every fueling in a range of Cairo days (inclusive), newest first, up to the cap. */
export function loadFuelings(range: DayRange, signal: AbortSignal, vehicleId?: string): Promise<Collected<Fueling>> {
  return collectPages((page, pageSignal) => companyApi.fuelings({ ...page, from: cairoDayStart(range.from), to: cairoDayStart(range.to, 1), vehicleId }, pageSignal), {
    max: FUELING_CAP,
    signal,
  });
}

export function useCompanyFuelings(range: DayRange | undefined, vehicleId?: string) {
  return useResource(
    (signal) => (range ? loadFuelings(range, signal, vehicleId) : Promise.resolve<Collected<Fueling>>({ items: [], truncated: false })),
    [range?.from, range?.to, vehicleId],
  );
}

export function useCompanyVehicles() {
  return useResource((signal) => collectPages<Vehicle>((page, s) => companyApi.vehicles(page, s), { max: VEHICLE_CAP, signal }), []);
}

export function useCompanyDepartments() {
  return useResource((signal) => companyApi.departments({ limit: 100 }, signal), []);
}

/** The last 30 Cairo days and this month so far, in one activity request. */
export function activityWindow(now: Date = new Date()): { from: string; to: string; last30: DayRange; month: DayRange } {
  const last30 = periodRange('LAST_30_DAYS', now);
  const month = periodRange('THIS_MONTH', now);
  return { from: last30.from < month.from ? last30.from : month.from, to: cairoDay(now), last30, month };
}
