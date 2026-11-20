import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { clearShift, loadShift, recordShiftEntry, summariseShift, type ShiftEntry } from './shift-log';

const entry = (overrides: Partial<ShiftEntry>): ShiftEntry => ({
  id: crypto.randomUUID(),
  at: '2026-11-15T08:00:00Z',
  plate: 'ق ر 1',
  fuelType: 'GAS_92',
  volumeMl: 20000,
  amountPiastres: 34500,
  unitPricePiastres: 1725,
  billable: true,
  status: 'COMPLETED',
  reference: 'F-1',
  receiptLocale: 'ar',
  ...overrides,
});

describe('summariseShift (end of shift)', () => {
  it('adds fuelings, litres and amounts by fuel in integers', () => {
    const entries = [
      entry({ volumeMl: 20333, amountPiastres: 35074 }),
      entry({ fuelType: 'DIESEL', volumeMl: 61000, amountPiastres: 94550, unitPricePiastres: 1550 }),
      entry({ volumeMl: 10001, amountPiastres: 17252 }),
      entry({ status: 'DECLINED', volumeMl: 30000, amountPiastres: 0, declineReason: 'COOLDOWN' }),
    ];
    const summary = summariseShift(entries);
    expect(summary).toMatchObject({ fuelings: 3, declined: 1, waiting: 0, volumeMl: 91334, amountPiastres: 146876, notBilledPiastres: 0 });
    expect(summary.byFuel).toEqual([
      { fuelType: 'GAS_92', fuelings: 2, volumeMl: 30334, amountPiastres: 52326 },
      { fuelType: 'DIESEL', fuelings: 1, volumeMl: 61000, amountPiastres: 94550 },
    ]);
    expect(Number.isInteger(summary.volumeMl) && Number.isInteger(summary.amountPiastres)).toBe(true);
  });

  it('counts offline captures as dispensed, and as waiting while still queued', () => {
    const waiting = entry({ status: 'QUEUED' });
    const sent = entry({ status: 'QUEUED' });
    const summary = summariseShift([waiting, sent], new Set([waiting.id]));
    expect(summary).toMatchObject({ fuelings: 2, waiting: 1, volumeMl: 40000, amountPiastres: 69000 });
  });

  it('keeps VIP fuel in the amount and shows the part not billed', () => {
    const summary = summariseShift([entry({}), entry({ billable: false, amountPiastres: 50000 })]);
    expect(summary).toMatchObject({ amountPiastres: 84500, notBilledPiastres: 50000 });
  });

  it('is empty for an empty shift', () => {
    expect(summariseShift([])).toEqual({ fuelings: 0, declined: 0, waiting: 0, volumeMl: 0, amountPiastres: 0, notBilledPiastres: 0, byFuel: [] });
  });
});

describe('shift log storage', () => {
  beforeAll(() => {
    // Tests run in Node: a minimal in-memory localStorage.
    const store = new Map<string, string>();
    globalThis.localStorage = {
      getItem: (key: string) => store.get(key) ?? null,
      setItem: (key: string, value: string) => void store.set(key, value),
      removeItem: (key: string) => void store.delete(key),
      clear: () => store.clear(),
      key: (index: number) => [...store.keys()][index] ?? null,
      get length() {
        return store.size;
      },
    } as Storage;
  });
  const who = { deviceId: 'd1', agentId: 'a1', sessionExpiresAt: '2026-11-15T20:00:00Z' };
  beforeEach(() => clearShift());

  it('keeps the entries of one session and replaces a retried fueling', () => {
    const first = entry({ id: 'x1', status: 'QUEUED' });
    recordShiftEntry(who, first);
    recordShiftEntry(who, entry({ id: 'x2' }));
    recordShiftEntry(who, { ...first, status: 'COMPLETED' });
    const { entries } = loadShift(who);
    expect(entries.map((item) => [item.id, item.status])).toEqual([
      ['x2', 'COMPLETED'],
      ['x1', 'COMPLETED'],
    ]);
  });

  it('starts a new log for a new sign-in or another agent', () => {
    recordShiftEntry(who, entry({}));
    expect(loadShift({ ...who, sessionExpiresAt: '2026-11-16T20:00:00Z' }).entries).toEqual([]);
    recordShiftEntry(who, entry({}));
    expect(loadShift({ ...who, agentId: 'a2' }).entries).toEqual([]);
  });
});
