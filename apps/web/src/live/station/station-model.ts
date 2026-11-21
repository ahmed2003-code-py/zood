/**
 * Station portal arithmetic and health rules (pure, tested). Volumes are integer millilitres and
 * amounts integer piastres; nothing here divides.
 */
import type { Agent, Device, Fueling, Shift } from '../api';

// --------------------------------------------------------------------------------------- shifts

export interface ShiftTotals {
  /** Completed fuelings (online and offline). */
  fuelings: number;
  declined: number;
  volumeMl: number;
  amountPiastres: number;
}

export const EMPTY_TOTALS: ShiftTotals = { fuelings: 0, declined: 0, volumeMl: 0, amountPiastres: 0 };

/**
 * Whether a fueling belongs to a shift: same terminal, same agent, and pumped while the shift ran
 * (`deviceAt`, the time at the pump, so an offline capture counts in the shift it happened in).
 */
export function inShift(shift: Shift, fueling: Fueling, now: Date): boolean {
  if (fueling.deviceId !== shift.deviceId || fueling.agentWorkerId !== shift.workerId) return false;
  const at = new Date(fueling.deviceAt).getTime();
  const end = shift.endedAt ? new Date(shift.endedAt).getTime() : now.getTime();
  return at >= new Date(shift.startedAt).getTime() && at <= end;
}

/** Each shift's totals from the fuelings loaded for the period the shifts cover. */
export function shiftTotals(shifts: readonly Shift[], fuelings: readonly Fueling[], now: Date): Map<string, ShiftTotals> {
  const totals = new Map<string, ShiftTotals>(shifts.map((shift) => [shift.id, { ...EMPTY_TOTALS }]));
  for (const fueling of fuelings) {
    const shift = shifts.find((candidate) => inShift(candidate, fueling, now));
    if (!shift) continue;
    const line = totals.get(shift.id)!;
    if (fueling.status === 'DECLINED') {
      line.declined += 1;
      continue;
    }
    line.fuelings += 1;
    line.volumeMl += fueling.volumeMl;
    line.amountPiastres += fueling.amountPiastres;
  }
  return totals;
}

/** The instants the fuelings must cover to total these shifts: the first start to the last end. */
export function shiftsWindow(shifts: readonly Shift[], now: Date): { from: string; to: string } | undefined {
  if (shifts.length === 0) return undefined;
  const starts = shifts.map((shift) => new Date(shift.startedAt).getTime());
  const ends = shifts.map((shift) => (shift.endedAt ? new Date(shift.endedAt).getTime() : now.getTime()));
  // The API's upper bound is exclusive: one second past the last end.
  return { from: new Date(Math.min(...starts)).toISOString(), to: new Date(Math.max(...ends) + 1000).toISOString() };
}

/** Whole minutes a shift ran (or has run so far). */
export function shiftMinutes(shift: Shift, now: Date): number {
  const end = shift.endedAt ? new Date(shift.endedAt).getTime() : now.getTime();
  return Math.max(0, Math.floor((end - new Date(shift.startedAt).getTime()) / 60_000));
}

// --------------------------------------------------------------------------------------- health

export type DeviceHealth = 'ONLINE' | 'OFFLINE' | 'NEVER_SYNCED' | 'AWAITING_ACTIVATION' | 'CODE_EXPIRED' | 'REVOKED';

/** A terminal that has not synced for this long counts as offline on the station's screens. */
export const DEVICE_OFFLINE_AFTER_MS = 30 * 60_000;

/**
 * What a station manager needs to know about a terminal. An activation code that ran out is not
 * "awaiting activation": nobody can activate it until Operations issues a new code. The server says
 * so (`activationExpired`); a code that expires while the page is open is caught by its time too.
 */
export function deviceHealth(device: Device, now: Date): DeviceHealth {
  if (device.status === 'REVOKED') return 'REVOKED';
  if (device.status === 'PENDING_ACTIVATION') {
    const expired = device.activationExpired || (device.activationExpiresAt ? new Date(device.activationExpiresAt).getTime() <= now.getTime() : false);
    return expired ? 'CODE_EXPIRED' : 'AWAITING_ACTIVATION';
  }
  if (!device.lastSyncAt) return 'NEVER_SYNCED';
  return now.getTime() - new Date(device.lastSyncAt).getTime() > DEVICE_OFFLINE_AFTER_MS ? 'OFFLINE' : 'ONLINE';
}

export type AgentHealth = 'ACTIVE' | 'LOCKED' | 'MUST_CHANGE_PIN' | 'SUSPENDED' | 'DEACTIVATED';

/**
 * A locked agent is not "active" until the lock ends; a new agent must still choose a PIN. The
 * server's `locked` is read with `lockedUntil`, so a lock that ends while the page is open ends here.
 */
export function agentHealth(agent: Agent, now: Date): AgentHealth {
  if (agent.status === 'SUSPENDED') return 'SUSPENDED';
  if (agent.status === 'DEACTIVATED') return 'DEACTIVATED';
  const locked = agent.lockedUntil ? new Date(agent.lockedUntil).getTime() > now.getTime() : agent.locked;
  if (locked) return 'LOCKED';
  if (agent.mustChangePin) return 'MUST_CHANGE_PIN';
  return 'ACTIVE';
}

// ---------------------------------------------------------------------------------- date ranges

export type RangeProblem = 'FROM_AFTER_TO' | 'IN_FUTURE' | 'TOO_LONG';

/** At most a year at a time, as the exports allow. */
export const MAX_RANGE_DAYS = 366;

const dayNumber = (day: string) => {
  const [year, month, date] = day.split('-').map(Number) as [number, number, number];
  return Date.UTC(year, month - 1, date) / 86_400_000;
};

/** What is wrong with a range of Cairo days (YYYY-MM-DD, inclusive), if anything. */
export function rangeProblem(from: string, to: string, today: string): RangeProblem | undefined {
  const valid = (day: string) => /^\d{4}-\d{2}-\d{2}$/.test(day);
  if (valid(from) && valid(to) && from > to) return 'FROM_AFTER_TO';
  if ((valid(from) && from > today) || (valid(to) && to > today)) return 'IN_FUTURE';
  if (valid(from) && valid(to) && dayNumber(to) - dayNumber(from) + 1 > MAX_RANGE_DAYS) return 'TOO_LONG';
  return undefined;
}

// ----------------------------------------------------------------------------------- statement

/** One statement line as a CSV row; amounts in pounds with two decimals, from integer piastres. */
export function piastresToCsv(piastres: number): string {
  const sign = piastres < 0 ? '-' : '';
  const abs = Math.abs(piastres);
  return `${sign}${Math.trunc(abs / 100)}.${String(abs % 100).padStart(2, '0')}`;
}

/** RFC 4180 quoting, and a leading apostrophe for cells a spreadsheet would run as a formula. */
export function csvCell(value: string): string {
  const safe = /^[=+\-@\t\r]/.test(value) ? `'${value}` : value;
  return /[",\n\r]/.test(safe) ? `"${safe.replace(/"/g, '""')}"` : safe;
}
