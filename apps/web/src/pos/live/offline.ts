/**
 * Deciding a fueling on the terminal while it is offline (B5 contract §2). The checks and their
 * order are the server's (`FuelingRules`, B4 contract §3), run against the cached snapshot with the
 * terminal's own not-yet-synced captures applied on top (PRD §7.4: provisional decrement), so two
 * offline captures cannot together spend what one could not.
 *
 * The server decides again at sync and never reverses a capture; anything the snapshot got wrong
 * becomes an alert there. These functions are pure so the same answers can be tested here.
 */
import type { components } from '@/api/generated/schema';
import { amountForVolume } from '@/domain/money';

type S = components['schemas'];
export type Snapshot = S['PosSnapshot'];
export type SnapshotPrice = S['SnapshotPrice'];
export type FuelType = S['SnapshotVehicle']['fuelType'];
export type OfflineDecline =
  | 'UNKNOWN_TAG'
  | 'COOLDOWN'
  | 'TAG_INACTIVE'
  | 'VEHICLE_INACTIVE'
  | 'COMPANY_FROZEN'
  | 'FUEL_TYPE_MISMATCH'
  | 'FUEL_NOT_AVAILABLE'
  | 'LIMIT_EXCEEDED'
  | 'INSUFFICIENT_BALANCE';

/** A capture waiting in the terminal's queue. The UID is kept because the server needs it at sync. */
export interface QueuedCapture {
  clientTxnId: string;
  tagUid: string;
  digest: string;
  vehicleId: string;
  companyId: string;
  plate: string;
  fuelType: FuelType;
  volumeMl: number;
  unitPricePiastres: number;
  amountPiastres: number;
  billable: boolean;
  deviceAt: string;
  agentId: string;
  receiptLocale: 'ar' | 'en';
}

export type OfflineDecision =
  | { ok: false; reason: OfflineDecline; plate?: string; cooldownUntil?: string }
  | {
      ok: true;
      vehicleId: string;
      companyId: string;
      plate: string;
      fuelType: FuelType;
      unitPricePiastres: number;
      amountPiastres: number;
      billable: boolean;
    };

/** The UID as the server stores it: hex, upper case, without the separators readers print. */
export function normaliseUid(raw: string): string | undefined {
  const uid = raw.replace(/[\s:-]/g, '').toUpperCase();
  return /^[0-9A-F]{8,32}$/.test(uid) ? uid : undefined;
}

/** HMAC-SHA256(tagKey, UID), lower-case hex: how the snapshot names a tag without holding its UID. */
export async function tagDigest(tagKeyBase64: string, uid: string): Promise<string> {
  const keyBytes = Uint8Array.from(atob(tagKeyBase64), (c) => c.charCodeAt(0));
  const key = await crypto.subtle.importKey('raw', keyBytes, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const mac = new Uint8Array(await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(uid)));
  return Array.from(mac, (byte) => byte.toString(16).padStart(2, '0')).join('');
}

/** The price in force at `at`: the entry that took effect last; on a tie the station's override. */
export function priceAt(prices: readonly SnapshotPrice[], fuel: FuelType, at: Date): number | undefined {
  let best: SnapshotPrice | undefined;
  for (const entry of prices) {
    if (entry.fuelType !== fuel || new Date(entry.effectiveFrom).getTime() > at.getTime()) continue;
    const later = !best || new Date(entry.effectiveFrom).getTime() > new Date(best.effectiveFrom).getTime();
    const tieWins = best && entry.effectiveFrom === best.effectiveFrom && !entry.national && best.national;
    if (later || tieWins) best = entry;
  }
  return best?.pricePiastresPerLitre;
}

/**
 * Which period an instant falls in, in the station's zone: the date for a day, the Saturday that
 * starts the week (PRD: weeks start on Saturday), the month. Two instants with the same key share
 * the period, so the snapshot's `used` still applies.
 */
export function periodKey(at: Date, period: 'DAY' | 'WEEK' | 'MONTH', timeZone: string): string {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit', weekday: 'short' })
    .formatToParts(at)
    .reduce<Record<string, string>>((acc, part) => ({ ...acc, [part.type]: part.value }), {});
  const { year, month, day, weekday } = parts as { year: string; month: string; day: string; weekday: string };
  if (period === 'MONTH') return `${year}-${month}`;
  if (period === 'DAY') return `${year}-${month}-${day}`;
  const sinceSaturday = (['Sat', 'Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri'] as const).indexOf(weekday as 'Sat');
  const saturday = new Date(Date.UTC(Number(year), Number(month) - 1, Number(day) - sinceSaturday));
  return saturday.toISOString().slice(0, 10);
}

export function decideOffline(
  snapshot: Snapshot,
  queue: readonly QueuedCapture[],
  request: { digest: string; volumeMl: number; fuelType?: FuelType; now: Date },
): OfflineDecision {
  const { now } = request;
  const tag = snapshot.tags.find((candidate) => candidate.digest === request.digest);
  if (!tag) return { ok: false, reason: 'UNKNOWN_TAG' };
  const vehicle = snapshot.vehicles.find((candidate) => candidate.id === tag.vehicleId);
  const company = vehicle && snapshot.companies.find((candidate) => candidate.id === vehicle.companyId);
  if (!vehicle || !company) return { ok: false, reason: 'UNKNOWN_TAG' };
  const plate = vehicle.plate;
  const mine = queue.filter((item) => item.vehicleId === vehicle.id);
  const ours = queue.filter((item) => item.companyId === company.id);

  // 1. Cooldown: the tag's, or the one this terminal's own last capture of it started.
  let cooldown = tag.cooldownUntil ? new Date(tag.cooldownUntil).getTime() : 0;
  for (const item of mine) cooldown = Math.max(cooldown, new Date(item.deviceAt).getTime() + company.cooldownMinutes * 60_000);
  if (cooldown > now.getTime()) return { ok: false, reason: 'COOLDOWN', plate, cooldownUntil: new Date(cooldown).toISOString() };
  // 2–5. Tag, vehicle, company.
  if (tag.status !== 'ACTIVE') return { ok: false, reason: 'TAG_INACTIVE', plate };
  if (!vehicle.active) return { ok: false, reason: 'VEHICLE_INACTIVE', plate };
  if (!company.active) return { ok: false, reason: 'COMPANY_FROZEN', plate };
  // 6. Fuel.
  if (request.fuelType && request.fuelType !== vehicle.fuelType) return { ok: false, reason: 'FUEL_TYPE_MISMATCH', plate };
  const fuel = vehicle.fuelType;
  const unitPrice = snapshot.station.fuelTypes.includes(fuel) ? priceAt(snapshot.prices, fuel, now) : undefined;
  if (unitPrice === undefined) return { ok: false, reason: 'FUEL_NOT_AVAILABLE', plate };
  const amount = amountForVolume(request.volumeMl, unitPrice);
  const vip = snapshot.vipEnabled && vehicle.vip;
  const decided = { vehicleId: vehicle.id, companyId: company.id, plate, fuelType: fuel, unitPricePiastres: unitPrice };
  if (vip) return { ok: true, ...decided, amountPiastres: amount, billable: false };

  // 7. Limits: the snapshot's use if still in the same period, plus this terminal's queued captures.
  const zone = snapshot.station.timeZone;
  for (const limit of snapshot.limits.filter((candidate) => candidate.vehicleId === vehicle.id)) {
    const current = periodKey(now, limit.period, zone);
    const fromSnapshot = periodKey(new Date(snapshot.serverTime), limit.period, zone) === current ? limit.used : 0;
    const queued = mine
      .filter((item) => periodKey(new Date(item.deviceAt), limit.period, zone) === current)
      .reduce((sum, item) => sum + (limit.kind === 'VOLUME' ? item.volumeMl : item.amountPiastres), 0);
    const requested = limit.kind === 'VOLUME' ? request.volumeMl : amount;
    if (fromSnapshot + queued + requested > limit.maxValue) return { ok: false, reason: 'LIMIT_EXCEEDED', plate };
  }
  // 8. Wallet: the allowance less what this terminal has already spent of it offline.
  const spent = ours.filter((item) => item.billable).reduce((sum, item) => sum + item.amountPiastres, 0);
  if (amount > 0 && spent + amount > company.offlineAllowancePiastres) return { ok: false, reason: 'INSUFFICIENT_BALANCE', plate };
  return { ok: true, ...decided, amountPiastres: amount, billable: true };
}
