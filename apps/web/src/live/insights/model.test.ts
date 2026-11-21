import { describe, expect, it, vi } from 'vitest';
import type { ActivityDay, Department, Fueling, Limit, Vehicle } from '../api';
import {
  budgetRows,
  budgetState,
  collectPages,
  coverLevel,
  daysOfCover,
  limitUsage,
  litresCell,
  mapWithConcurrency,
  NO_DEPARTMENT,
  piastresCell,
  spendByDepartment,
  spendByFuel,
  spendByVehicle,
  statementInRange,
  sumActivity,
  toCsv,
} from './model';

const day = (amountPiastres: number, volumeMl = 0, completed = 1): ActivityDay => ({
  day: '2026-11-01',
  amountPiastres,
  volumeMl,
  completed,
  declined: 0,
  offline: 0,
  withConflicts: 0,
  alerts: 0,
});

let seq = 0;
const fueling = (overrides: Partial<Fueling>): Fueling => ({
  id: `f${++seq}`,
  reference: `FL-${seq}`,
  companyId: 'c1',
  companyNameAr: 'ش',
  companyNameEn: 'C',
  vehicleId: 'v1',
  plate: 'ق ط ر 1',
  stationId: 's1',
  stationNameAr: 'م',
  stationNameEn: 'S',
  operatorId: 'o1',
  deviceId: 'd1',
  agentWorkerId: 'W-1',
  fuelType: 'GAS_92',
  volumeMl: 40_000,
  unitPricePiastres: 1725,
  amountPiastres: 69_000,
  status: 'COMPLETED',
  origin: 'ONLINE',
  billable: true,
  conflicts: [],
  deviceAt: '2026-11-21T08:00:00Z',
  evaluatedAt: '2026-11-21T08:00:00Z',
  receivedAt: '2026-11-21T08:00:00Z',
  ...overrides,
});

const vehicle = (id: string, departmentId?: string): Vehicle => ({
  id,
  companyId: 'c1',
  departmentId,
  plate: id,
  make: 'Toyota',
  model: 'Hilux',
  fuelType: 'GAS_92',
  tankCapacityMl: 80_000,
  tier: 'NORMAL',
  status: 'ACTIVE',
  createdAt: '2026-01-01T00:00:00Z',
});

describe('days of cover', () => {
  it('divides the balance by the average daily spend, in integers', () => {
    // 30 days spent EGP 3,000.00 → EGP 100.00 a day; EGP 1,250.00 lasts 12 days.
    expect(daysOfCover(125_000, [day(300_000)], 30)).toBe(12);
    expect(daysOfCover(0, [day(300_000)], 30)).toBe(0);
  });

  it('has no figure when nothing was spent', () => {
    expect(daysOfCover(125_000, [day(0)], 30)).toBeUndefined();
    expect(daysOfCover(125_000, [], 30)).toBeUndefined();
  });

  it('grades the cover', () => {
    expect(coverLevel(undefined)).toBe('good');
    expect(coverLevel(10)).toBe('good');
    expect(coverLevel(5)).toBe('low');
    expect(coverLevel(1)).toBe('critical');
  });

  it('sums activity days', () => {
    expect(sumActivity([day(100, 1_000, 2), day(50, 500, 1)])).toEqual({ amountPiastres: 150, volumeMl: 1_500, fuelings: 3, declined: 0 });
  });
});

describe('spend breakdowns', () => {
  const fuelings = [
    fueling({ vehicleId: 'v1', amountPiastres: 100_00, volumeMl: 10_000 }),
    fueling({ vehicleId: 'v1', amountPiastres: 50_00, volumeMl: 5_000, fuelType: 'DIESEL' }),
    fueling({ vehicleId: 'v2', amountPiastres: 300_00, volumeMl: 20_000 }),
    fueling({ vehicleId: 'v3', amountPiastres: 70_00, volumeMl: 7_000, billable: false }),
    fueling({ vehicleId: 'v2', amountPiastres: 0, volumeMl: 0, status: 'DECLINED' }),
  ];
  const vehicles = [vehicle('v1', 'd1'), vehicle('v2', 'd2'), vehicle('v3')];

  it('counts only billable completed fuelings as spend, every completed one as litres', () => {
    const rows = spendByVehicle(fuelings);
    expect(rows.map((row) => [row.key, row.amountPiastres, row.volumeMl, row.fuelings])).toEqual([
      ['v2', 300_00, 20_000, 1],
      ['v1', 150_00, 15_000, 2],
      ['v3', 0, 7_000, 1],
    ]);
  });

  it('groups by fuel type and by department through the vehicle', () => {
    expect(spendByFuel(fuelings).map((row) => [row.key, row.amountPiastres])).toEqual([
      ['GAS_92', 400_00],
      ['DIESEL', 50_00],
    ]);
    expect(spendByDepartment(fuelings, vehicles).map((row) => [row.key, row.amountPiastres])).toEqual([
      ['d2', 300_00],
      ['d1', 150_00],
      [NO_DEPARTMENT, 0],
    ]);
  });

  it('puts departments over budget first and keeps unassigned vehicles last', () => {
    const departments: Department[] = [
      { id: 'd1', name: 'Sales', active: true, monthlyBudgetPiastres: 100_00 },
      { id: 'd2', name: 'Distribution', active: true, monthlyBudgetPiastres: 1_000_00 },
      { id: 'd3', name: 'Admin', active: true },
    ];
    const rows = budgetRows(departments, spendByDepartment(fuelings, vehicles));
    expect(rows.map((row) => [row.key, row.state, row.percent])).toEqual([
      ['d1', 'over', 150],
      ['d2', 'ok', 30],
      ['d3', 'none', undefined],
      [NO_DEPARTMENT, 'none', undefined],
    ]);
  });

  it('grades a budget at 80 % and past 100 %', () => {
    expect(budgetState(79, 100)).toBe('ok');
    expect(budgetState(80, 100)).toBe('near');
    expect(budgetState(100, 100)).toBe('near');
    expect(budgetState(101, 100)).toBe('over');
    expect(budgetState(500, undefined)).toBe('none');
  });
});

describe('limit usage', () => {
  // Saturday 21 November 2026, 10:00 in Cairo.
  const now = new Date('2026-11-21T08:00:00Z');
  const limits: Limit[] = [
    { id: 'l1', kind: 'VOLUME', period: 'DAY', maxValue: 50_000, createdAt: '2026-01-01T00:00:00Z' },
    { id: 'l2', kind: 'AMOUNT', period: 'MONTH', maxValue: 200_00, createdAt: '2026-01-01T00:00:00Z' },
    { id: 'l3', kind: 'VOLUME', period: 'WEEK', maxValue: 60_000, createdAt: '2026-01-01T00:00:00Z' },
  ];
  const fuelings = [
    fueling({ evaluatedAt: '2026-11-21T06:00:00Z', volumeMl: 30_000, amountPiastres: 100_00 }),
    fueling({ evaluatedAt: '2026-11-20T06:00:00Z', volumeMl: 25_000, amountPiastres: 90_00 }),
    fueling({ evaluatedAt: '2026-10-31T06:00:00Z', volumeMl: 10_000, amountPiastres: 30_00 }),
    fueling({ evaluatedAt: '2026-11-21T07:00:00Z', volumeMl: 0, amountPiastres: 0, status: 'DECLINED' }),
  ];

  it('counts each limit over its own Cairo period (weeks start on Saturday)', () => {
    const [daily, monthly, weekly] = limitUsage(limits, fuelings, now);
    expect([daily?.used, daily?.remaining, daily?.percent, daily?.state]).toEqual([30_000, 20_000, 60, 'ok']);
    expect([monthly?.used, monthly?.percent, monthly?.state]).toEqual([190_00, 95, 'near']);
    // The week began today (Saturday), so Friday's fueling is last week's.
    expect([weekly?.used, weekly?.state]).toEqual([30_000, 'ok']);
  });

  it('marks a reached limit', () => {
    const [daily] = limitUsage([limits[0]!], [fueling({ evaluatedAt: '2026-11-21T06:00:00Z', volumeMl: 50_000 })], now);
    expect(daily?.state).toBe('reached');
    expect(daily?.remaining).toBe(0);
  });
});

describe('collecting pages', () => {
  it('follows the cursor up to the cap and says when it stopped early', async () => {
    const load = vi.fn(async ({ cursor }: { cursor?: string }) => {
      const index = Number(cursor ?? 0);
      return { items: [index * 2, index * 2 + 1], nextCursor: index < 9 ? String(index + 1) : undefined };
    });
    const all = await collectPages(load, { pageSize: 2, max: 100 });
    expect(all.items).toHaveLength(20);
    expect(all.truncated).toBe(false);
    const capped = await collectPages(load, { pageSize: 2, max: 5 });
    expect(capped.items).toHaveLength(5);
    expect(capped.truncated).toBe(true);
  });
});

describe('CSV', () => {
  it('quotes every field, defuses formulas and starts with a byte-order mark', () => {
    const text = toCsv([
      ['Plate', 'Amount'],
      ['=HYPERLINK("x")', '1250.50'],
      ['ق ط ر "1"', 3],
    ]);
    expect(text).toBe('\uFEFF"Plate","Amount"\r\n"\'=HYPERLINK(""x"")","1250.50"\r\n"ق ط ر ""1""","3"\r\n');
  });

  it('writes money and litres exactly', () => {
    expect(piastresCell(125_050)).toBe('1250.50');
    expect(piastresCell(-5)).toBe('-0.05');
    expect(litresCell(42_500)).toBe('42.500');
  });
});

describe('statement by date', () => {
  const entries = [
    { id: 'e1', postedAt: '2026-11-21T08:00:00Z' },
    { id: 'e2', postedAt: '2026-11-20T08:00:00Z' },
    { id: 'e3', postedAt: '2026-11-10T08:00:00Z' },
    { id: 'e4', postedAt: '2026-10-31T22:30:00Z' }, // 00:30 on 1 November in Cairo (UTC+2)
    { id: 'e5', postedAt: '2026-10-30T08:00:00Z' },
    { id: 'e6', postedAt: '2026-10-01T08:00:00Z' },
  ];
  const load = vi.fn(async ({ cursor }: { cursor?: string }) => {
    const start = Number(cursor ?? 0);
    return { items: entries.slice(start, start + 2), nextCursor: start + 2 < entries.length ? String(start + 2) : undefined };
  });

  it('keeps the Cairo days in range and stops reading once past the start', async () => {
    load.mockClear();
    const result = await statementInRange(load, { from: '2026-11-01', to: '2026-11-20' }, { pageSize: 2 });
    expect(result.items.map((entry) => entry.id)).toEqual(['e2', 'e3', 'e4']);
    expect(result.truncated).toBe(false);
    expect(load).toHaveBeenCalledTimes(3);
  });
});

describe('limited concurrency', () => {
  it('keeps the order and never runs more than asked at once', async () => {
    let running = 0;
    let peak = 0;
    const result = await mapWithConcurrency([5, 1, 4, 2, 3], 2, async (value) => {
      running += 1;
      peak = Math.max(peak, running);
      await new Promise((resolve) => setTimeout(resolve, value));
      running -= 1;
      return value * 10;
    });
    expect(result).toEqual([50, 10, 40, 20, 30]);
    expect(peak).toBe(2);
  });
});
