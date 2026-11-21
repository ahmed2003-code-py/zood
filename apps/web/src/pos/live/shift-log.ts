/**
 * The agent's own record of this shift on this terminal: every fueling recorded, refused or saved
 * offline since the agent signed in, for the shift history and the end-of-shift summary.
 *
 * The POS API has no "my shift" endpoint, and the server's figures are for the station portal; this
 * log is what the agent saw on this terminal. It is kept per device and per agent session, survives
 * a reload, and is cleared when the shift ends. Volumes are integer millilitres and amounts integer
 * piastres; the summary only adds integers.
 */
import type { FuelType, OfflineDecline } from './offline';

export type ShiftEntryStatus = 'COMPLETED' | 'DECLINED' | 'QUEUED';

export interface ShiftEntry {
  /** The clientTxnId: one entry per fueling, whatever happens to it later. */
  id: string;
  at: string;
  plate: string;
  fuelType?: FuelType;
  volumeMl: number;
  /** The fuel's value at the pump, also for a VIP vehicle (the station is paid for it). */
  amountPiastres: number;
  unitPricePiastres: number;
  billable: boolean;
  status: ShiftEntryStatus;
  declineReason?: OfflineDecline;
  /** The server's reference (online) or the terminal's short one (offline). */
  reference: string;
  /** The public receipt link, once the server has the fueling. */
  receiptPath?: string;
  receiptLocale: 'ar' | 'en';
  stationNameAr?: string;
  stationNameEn?: string;
}

export interface FuelTotals {
  fuelType: FuelType;
  fuelings: number;
  volumeMl: number;
  amountPiastres: number;
}

export interface ShiftSummary {
  /** Completed and saved-offline fuelings: fuel that left the pump. */
  fuelings: number;
  declined: number;
  /** Saved on the terminal and not yet accepted by the server. */
  waiting: number;
  volumeMl: number;
  amountPiastres: number;
  /** The part of the amount not billed to companies (VIP). */
  notBilledPiastres: number;
  /** One line per fuel, in the order the fuels were first dispensed. */
  byFuel: FuelTotals[];
}

/**
 * Totals for the shift. `pending` holds the ids still in the offline queue: a QUEUED entry that has
 * left the queue was accepted or refused by the server, and only the server knows which, so it still
 * counts as dispensed (the fuel left the pump either way) but no longer as waiting.
 */
export function summariseShift(entries: readonly ShiftEntry[], pending: ReadonlySet<string> = new Set()): ShiftSummary {
  const summary: ShiftSummary = { fuelings: 0, declined: 0, waiting: 0, volumeMl: 0, amountPiastres: 0, notBilledPiastres: 0, byFuel: [] };
  const lines = new Map<FuelType, FuelTotals>();
  for (const entry of entries) {
    if (entry.status === 'DECLINED') {
      summary.declined += 1;
      continue;
    }
    if (entry.status === 'QUEUED' && pending.has(entry.id)) summary.waiting += 1;
    summary.fuelings += 1;
    summary.volumeMl += entry.volumeMl;
    summary.amountPiastres += entry.amountPiastres;
    if (!entry.billable) summary.notBilledPiastres += entry.amountPiastres;
    if (!entry.fuelType) continue;
    const line = lines.get(entry.fuelType) ?? { fuelType: entry.fuelType, fuelings: 0, volumeMl: 0, amountPiastres: 0 };
    line.fuelings += 1;
    line.volumeMl += entry.volumeMl;
    line.amountPiastres += entry.amountPiastres;
    lines.set(entry.fuelType, line);
  }
  summary.byFuel = [...lines.values()];
  return summary;
}

// ------------------------------------------------------------------------------------- storage

const KEY = 'zawed.pos.shift';
const VERSION = 1;

interface StoredShift {
  v: typeof VERSION;
  deviceId: string;
  agentId: string;
  /** The session's expiry: a new sign-in is a new shift. */
  sessionExpiresAt: string;
  startedAt: string;
  entries: ShiftEntry[];
}

export interface ShiftIdentity {
  deviceId: string;
  agentId: string;
  sessionExpiresAt: string;
}

function read(): StoredShift | undefined {
  try {
    const raw = localStorage.getItem(KEY);
    const parsed = raw ? (JSON.parse(raw) as StoredShift) : undefined;
    return parsed?.v === VERSION ? parsed : undefined;
  } catch {
    return undefined;
  }
}

function write(shift: StoredShift | undefined): void {
  try {
    if (shift) localStorage.setItem(KEY, JSON.stringify(shift));
    else localStorage.removeItem(KEY);
  } catch {
    // Storage blocked: the history lasts as long as the page.
  }
}

const same = (shift: StoredShift, who: ShiftIdentity) =>
  shift.deviceId === who.deviceId && shift.agentId === who.agentId && shift.sessionExpiresAt === who.sessionExpiresAt;

/** This shift's log; a different agent or a new sign-in starts an empty one. */
export function loadShift(who: ShiftIdentity, now: Date = new Date()): { startedAt: string; entries: ShiftEntry[] } {
  const stored = read();
  if (stored && same(stored, who)) return { startedAt: stored.startedAt, entries: stored.entries };
  const fresh: StoredShift = { v: VERSION, ...who, startedAt: now.toISOString(), entries: [] };
  write(fresh);
  return { startedAt: fresh.startedAt, entries: [] };
}

/** Adds an entry, or replaces the one with the same id (a retry of the same fueling). */
export function recordShiftEntry(who: ShiftIdentity, entry: ShiftEntry): ShiftEntry[] {
  const stored = read();
  const base: StoredShift = stored && same(stored, who) ? stored : { v: VERSION, ...who, startedAt: entry.at, entries: [] };
  const entries = [...base.entries.filter((item) => item.id !== entry.id), entry];
  write({ ...base, entries });
  return entries;
}

export function clearShift(): void {
  write(undefined);
}
