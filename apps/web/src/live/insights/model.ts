/**
 * The figures behind the home dashboards, budgets, limits and reports, computed from the API's
 * existing lists (fuelings, vehicles, departments, activity). Integer piastres and millilitres
 * throughout: sums and comparisons only, no floating point on money except a display percentage.
 * Kept free of React so every rule here is unit-tested.
 */
import type { ActivityDay, Department, Fueling, Limit, Page, PageQuery, Vehicle } from '../api';
import { cairoDay, periodRange } from '@/lib/periods';

/** Fuelings that cost the company money: completed and billable (a VIP vehicle's are not billed). */
export function spends(fueling: Fueling): boolean {
  return fueling.status === 'COMPLETED' && fueling.billable;
}

/** Litres actually dispensed: every completed fueling, billed or not. */
export function dispensed(fueling: Fueling): boolean {
  return fueling.status === 'COMPLETED';
}

export interface Collected<T> {
  items: T[];
  /** More rows exist beyond the cap: the figures cover the newest `items.length` only. */
  truncated: boolean;
}

/**
 * Reads a cursor-paginated list page by page (100 at a time, the API's maximum) up to `max` rows.
 * A pilot fleet fits well inside the cap; beyond it the screen says the figures are partial.
 */
export async function collectPages<T>(
  load: (page: PageQuery, signal?: AbortSignal) => Promise<Page<T>>,
  { max = 2000, pageSize = 100, signal }: { max?: number; pageSize?: number; signal?: AbortSignal } = {},
): Promise<Collected<T>> {
  const items: T[] = [];
  let cursor: string | undefined;
  do {
    const page = await load({ cursor, limit: pageSize }, signal);
    items.push(...page.items);
    cursor = page.nextCursor ?? undefined;
  } while (cursor && items.length < max && !signal?.aborted);
  return { items: items.slice(0, max), truncated: Boolean(cursor) || items.length > max };
}

export interface Totals {
  amountPiastres: number;
  volumeMl: number;
  fuelings: number;
  declined: number;
}

export function sumActivity(days: readonly ActivityDay[]): Totals {
  return days.reduce<Totals>(
    (sum, day) => ({
      amountPiastres: sum.amountPiastres + day.amountPiastres,
      volumeMl: sum.volumeMl + day.volumeMl,
      fuelings: sum.fuelings + day.completed,
      declined: sum.declined + day.declined,
    }),
    { amountPiastres: 0, volumeMl: 0, fuelings: 0, declined: 0 },
  );
}

/**
 * How many days the balance lasts at the average daily spend of the window (normally the last 30
 * days, today included). Undefined when nothing was spent: there is no rate to divide by.
 */
export function daysOfCover(balancePiastres: number, days: readonly ActivityDay[], windowDays = 30): number | undefined {
  const spent = sumActivity(days).amountPiastres;
  if (spent <= 0 || windowDays <= 0) return undefined;
  if (balancePiastres <= 0) return 0;
  // balance / (spent / window) = balance * window / spent, in integers.
  return Math.floor((balancePiastres * windowDays) / spent);
}

export type CoverLevel = 'good' | 'low' | 'critical';

/** A week or more is comfortable; under three days is urgent. */
export function coverLevel(days: number | undefined): CoverLevel {
  if (days === undefined || days >= 7) return 'good';
  return days >= 3 ? 'low' : 'critical';
}

export interface SpendRow {
  key: string;
  amountPiastres: number;
  volumeMl: number;
  fuelings: number;
}

function addTo(map: Map<string, SpendRow>, key: string, fueling: Fueling): void {
  const row = map.get(key) ?? { key, amountPiastres: 0, volumeMl: 0, fuelings: 0 };
  if (dispensed(fueling)) {
    row.volumeMl += fueling.volumeMl;
    row.fuelings += 1;
  }
  if (spends(fueling)) row.amountPiastres += fueling.amountPiastres;
  map.set(key, row);
}

const bySpend = (a: SpendRow, b: SpendRow) => b.amountPiastres - a.amountPiastres || b.volumeMl - a.volumeMl || a.key.localeCompare(b.key);

/** Spend per vehicle, highest first. */
export function spendByVehicle(fuelings: readonly Fueling[]): SpendRow[] {
  const map = new Map<string, SpendRow>();
  for (const fueling of fuelings) if (dispensed(fueling)) addTo(map, fueling.vehicleId, fueling);
  return [...map.values()].sort(bySpend);
}

/** Spend per fuel type, highest first. */
export function spendByFuel(fuelings: readonly Fueling[]): SpendRow[] {
  const map = new Map<string, SpendRow>();
  for (const fueling of fuelings) if (dispensed(fueling)) addTo(map, fueling.fuelType, fueling);
  return [...map.values()].sort(bySpend);
}

/** The key for vehicles without a department. */
export const NO_DEPARTMENT = '';

/** Spend per department through each fueling's vehicle; a vehicle with no department counts under NO_DEPARTMENT. */
export function spendByDepartment(fuelings: readonly Fueling[], vehicles: readonly Vehicle[]): SpendRow[] {
  const department = new Map(vehicles.map((vehicle) => [vehicle.id, vehicle.departmentId ?? NO_DEPARTMENT]));
  const map = new Map<string, SpendRow>();
  for (const fueling of fuelings) if (dispensed(fueling)) addTo(map, department.get(fueling.vehicleId) ?? NO_DEPARTMENT, fueling);
  return [...map.values()].sort(bySpend);
}

export type BudgetState = 'none' | 'ok' | 'near' | 'over';

export interface BudgetRow {
  department: Department | undefined;
  key: string;
  budgetPiastres?: number;
  spentPiastres: number;
  volumeMl: number;
  /** Whole percent of the budget used (display only), or undefined without a budget. */
  percent?: number;
  state: BudgetState;
}

/** 80 % of a budget or more is "near"; past it is "over". */
export function budgetState(spent: number, budget: number | undefined): BudgetState {
  if (budget === undefined || budget <= 0) return 'none';
  if (spent > budget) return 'over';
  return spent * 100 >= budget * 80 ? 'near' : 'ok';
}

/** Every active department with its monthly budget and this period's spend, then unassigned vehicles. */
export function budgetRows(departments: readonly Department[], spend: readonly SpendRow[]): BudgetRow[] {
  const spent = new Map(spend.map((row) => [row.key, row]));
  const rows: BudgetRow[] = departments
    .filter((department) => department.active || spent.has(department.id))
    .map((department) => {
      const row = spent.get(department.id);
      const budget = department.monthlyBudgetPiastres ?? undefined;
      const spentPiastres = row?.amountPiastres ?? 0;
      return {
        department,
        key: department.id,
        budgetPiastres: budget,
        spentPiastres,
        volumeMl: row?.volumeMl ?? 0,
        percent: budget && budget > 0 ? Math.round((spentPiastres * 100) / budget) : undefined,
        state: budgetState(spentPiastres, budget),
      };
    });
  const unassigned = spent.get(NO_DEPARTMENT);
  if (unassigned && (unassigned.amountPiastres > 0 || unassigned.volumeMl > 0)) {
    rows.push({ department: undefined, key: NO_DEPARTMENT, spentPiastres: unassigned.amountPiastres, volumeMl: unassigned.volumeMl, state: 'none' });
  }
  const order: Record<BudgetState, number> = { over: 0, near: 1, ok: 2, none: 3 };
  return rows.sort((a, b) => order[a.state] - order[b.state] || b.spentPiastres - a.spentPiastres);
}

export interface LimitUsage {
  limit: Limit;
  /** What the period has used so far: millilitres for a VOLUME limit, piastres for an AMOUNT limit. */
  used: number;
  remaining: number;
  /** Whole percent (display only), capped at 999. */
  percent: number;
  state: 'ok' | 'near' | 'reached';
}

/** The Cairo days a limit period covers right now (weeks start on Saturday, as the API's do). */
export function limitPeriodDays(period: Limit['period'], now: Date): { from: string; to: string } {
  return periodRange(period === 'DAY' ? 'TODAY' : period === 'WEEK' ? 'THIS_WEEK' : 'THIS_MONTH', now);
}

/**
 * How much of each limit one vehicle has used in its current period, from that vehicle's fuelings
 * (completed ones only: a decline dispensed nothing). An AMOUNT limit counts what was dispensed at
 * its price even when the vehicle is not billed, because the API checks limits the same way.
 */
export function limitUsage(limits: readonly Limit[], fuelings: readonly Fueling[], now: Date): LimitUsage[] {
  return limits.map((limit) => {
    const { from, to } = limitPeriodDays(limit.period, now);
    const used = fuelings
      .filter((fueling) => dispensed(fueling))
      .filter((fueling) => {
        const day = cairoDay(new Date(fueling.evaluatedAt));
        return day >= from && day <= to;
      })
      .reduce((sum, fueling) => sum + (limit.kind === 'VOLUME' ? fueling.volumeMl : fueling.amountPiastres), 0);
    const percent = limit.maxValue > 0 ? Math.min(999, Math.round((used * 100) / limit.maxValue)) : 0;
    return {
      limit,
      used,
      remaining: Math.max(0, limit.maxValue - used),
      percent,
      state: used >= limit.maxValue ? 'reached' : used * 100 >= limit.maxValue * 80 ? 'near' : 'ok',
    };
  });
}

/** The fuelings of one vehicle. */
export function fuelingsOf(fuelings: readonly Fueling[], vehicleId: string): Fueling[] {
  return fuelings.filter((fueling) => fueling.vehicleId === vehicleId);
}

/**
 * A CSV file's text: UTF-8 with a byte-order mark so Excel reads Arabic, CRLF line ends, every
 * field quoted, and a leading = + - @ (or tab / carriage return) defused so a spreadsheet never runs
 * a cell as a formula.
 */
export function toCsv(rows: ReadonlyArray<ReadonlyArray<string | number>>): string {
  const cell = (value: string | number) => {
    let text = String(value);
    if (typeof value === 'string' && /^[=+\-@\t\r]/.test(text)) text = `'${text}`;
    return `"${text.replace(/"/g, '""')}"`;
  };
  return `\uFEFF${rows.map((row) => row.map(cell).join(',')).join('\r\n')}\r\n`;
}

/** Piastres as a plain decimal for a CSV cell ("1250.50"), without floating point. */
export function piastresCell(value: number): string {
  const sign = value < 0 ? '-' : '';
  const abs = Math.abs(value);
  return `${sign}${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, '0')}`;
}

/** Millilitres as litres with three decimals for a CSV cell ("42.500"). */
export function litresCell(value: number): string {
  const sign = value < 0 ? '-' : '';
  const abs = Math.abs(value);
  return `${sign}${Math.floor(abs / 1000)}.${String(abs % 1000).padStart(3, '0')}`;
}

/** Hands a CSV text to the browser as a download. */
export function downloadCsv(text: string, fileName: string): void {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/**
 * Wallet entries posted on Cairo days `from`…`to` (inclusive). The statement is newest first and
 * has no date filter on the API, so this reads pages until it passes `from` (or reaches the cap).
 */
export async function statementInRange<T extends { postedAt: string }>(
  load: (page: PageQuery, signal?: AbortSignal) => Promise<Page<T>>,
  range: { from: string; to: string },
  { max = 5000, pageSize = 100, signal }: { max?: number; pageSize?: number; signal?: AbortSignal } = {},
): Promise<Collected<T>> {
  const inRange: T[] = [];
  let cursor: string | undefined;
  let read = 0;
  let passedStart = false;
  do {
    const page = await load({ cursor, limit: pageSize }, signal);
    read += page.items.length;
    for (const entry of page.items) {
      const day = cairoDay(new Date(entry.postedAt));
      if (range.from && day < range.from) {
        passedStart = true;
        break;
      }
      if (!range.to || day <= range.to) inRange.push(entry);
    }
    cursor = page.nextCursor ?? undefined;
  } while (cursor && !passedStart && read < max && !signal?.aborted);
  return { items: inRange, truncated: !passedStart && Boolean(cursor) };
}

/** Runs `task` over `items` with at most `concurrency` at a time, keeping the input order. */
export async function mapWithConcurrency<T, R>(items: readonly T[], concurrency: number, task: (item: T) => Promise<R>): Promise<R[]> {
  const results = new Array<R>(items.length);
  let next = 0;
  const worker = async () => {
    while (next < items.length) {
      const index = next++;
      results[index] = await task(items[index]!);
    }
  };
  await Promise.all(Array.from({ length: Math.max(1, Math.min(concurrency, items.length)) }, worker));
  return results;
}
