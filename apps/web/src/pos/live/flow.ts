/**
 * What the live POS tells the agent between reading a tag and recording a fueling (pure, tested).
 *
 * After the tag read the terminal looks the tag up in its snapshot (B5 contract §2) and shows the
 * agent what it knows: plate, company, fuel, the wallet as a status word, what is left of the
 * vehicle's limits and any cooldown. A condition that would certainly stop the fueling (cooldown,
 * a blocked tag, vehicle or company, a used-up limit, a fuel the station does not sell) ends the
 * flow on a decline screen before the agent enters a volume. Online, the server still decides the
 * fueling itself (B4 contract §3); these checks only spare the agent a pointless entry, and the
 * flow refreshes the snapshot once before trusting a refusal (see FuelFlow).
 *
 * Amounts are integer piastres and volumes integer millilitres (src/domain/money.ts).
 */
import { amountForVolume } from '@/domain/money';
import { MAX_SNAPSHOT_AGE_MS, periodKey, priceAt, type FuelType, type OfflineDecline, type QueuedCapture, type Snapshot } from './offline';

export type WalletWord = Snapshot['companies'][number]['walletStatus'];
export type LimitKind = 'VOLUME' | 'AMOUNT';
export type LimitPeriod = 'DAY' | 'WEEK' | 'MONTH';

/** What is left of one limit of the vehicle, after this terminal's own captures since the snapshot. */
export interface LimitLeft {
  kind: LimitKind;
  period: LimitPeriod;
  /** Millilitres for a volume limit, piastres for an amount limit; never below zero. */
  remaining: number;
}

/** A tag the snapshot knows. `block` is set when the fueling cannot go ahead. */
export interface KnownTag {
  known: true;
  vehicleId: string;
  companyId: string;
  plate: string;
  companyNameAr: string;
  companyNameEn: string;
  fuelType: FuelType;
  walletStatus: WalletWord;
  /** VIP vehicles (when the operator allows it) are not billed to the company. */
  billable: boolean;
  tankCapacityMl: number;
  /** Price per litre now; absent when the station does not sell the vehicle's fuel. */
  unitPricePiastres?: number;
  /** Strictest first: the day's limit before the week's and the month's. */
  limits: LimitLeft[];
  /** Set while a cooldown runs (the tag's own, or one this terminal's last capture started). */
  cooldownUntil?: string;
  block?: OfflineDecline;
}

/** A tag the terminal cannot check. Online the server decides; offline it is a refusal. */
export interface UnknownTag {
  known: false;
  reason: 'UNKNOWN_TAG' | 'NO_SNAPSHOT' | 'SNAPSHOT_STALE' | 'STATION_CLOSED';
}

export type TagCheck = KnownTag | UnknownTag;

const PERIOD_ORDER: Record<LimitPeriod, number> = { DAY: 0, WEEK: 1, MONTH: 2 };

/**
 * Looks a tag up in the snapshot, with this terminal's not-yet-synced captures applied on top (the
 * same provisional decrement `decideOffline` uses, so the two never disagree).
 */
export function checkTag(snapshot: Snapshot | undefined, queue: readonly QueuedCapture[], digest: string, now: Date): TagCheck {
  if (!snapshot) return { known: false, reason: 'NO_SNAPSHOT' };
  if (now.getTime() - new Date(snapshot.serverTime).getTime() > MAX_SNAPSHOT_AGE_MS) return { known: false, reason: 'SNAPSHOT_STALE' };
  if (!snapshot.station.open) return { known: false, reason: 'STATION_CLOSED' };
  const matches = snapshot.tags.filter((candidate) => candidate.digest === digest);
  const tag = matches.find((candidate) => candidate.status === 'ACTIVE') ?? matches[0];
  const vehicle = tag && snapshot.vehicles.find((candidate) => candidate.id === tag.vehicleId);
  const company = vehicle && snapshot.companies.find((candidate) => candidate.id === vehicle.companyId);
  if (!tag || !vehicle || !company) return { known: false, reason: 'UNKNOWN_TAG' };

  const mine = queue.filter((item) => item.vehicleId === vehicle.id);
  let cooldown = tag.cooldownUntil ? new Date(tag.cooldownUntil).getTime() : 0;
  for (const item of mine) cooldown = Math.max(cooldown, new Date(item.deviceAt).getTime() + company.cooldownMinutes * 60_000);
  const cooldownUntil = cooldown > now.getTime() ? new Date(cooldown).toISOString() : undefined;

  const sells = snapshot.station.fuelTypes.includes(vehicle.fuelType);
  const unitPricePiastres = sells ? priceAt(snapshot.prices, vehicle.fuelType, now) : undefined;
  const billable = !(snapshot.vipEnabled && vehicle.vip);

  const zone = snapshot.station.timeZone;
  const limits: LimitLeft[] = [];
  if (billable) {
    for (const limit of snapshot.limits.filter((candidate) => candidate.vehicleId === vehicle.id)) {
      const current = periodKey(now, limit.period, zone);
      // A new period has begun since the snapshot: what is left is not known here.
      if (periodKey(new Date(snapshot.serverTime), limit.period, zone) !== current) continue;
      const used = mine
        .filter((item) => periodKey(new Date(item.deviceAt), limit.period, zone) === current)
        .reduce((sum, item) => sum + (limit.kind === 'VOLUME' ? item.volumeMl : item.amountPiastres), 0);
      limits.push({ kind: limit.kind, period: limit.period, remaining: Math.max(0, limit.remaining - used) });
    }
    limits.sort((a, b) => PERIOD_ORDER[a.period] - PERIOD_ORDER[b.period]);
  }

  // The server's order (B4 contract §3): cooldown, tag, vehicle, company, fuel, limits.
  const block: OfflineDecline | undefined = cooldownUntil
    ? 'COOLDOWN'
    : tag.status !== 'ACTIVE'
      ? 'TAG_INACTIVE'
      : !vehicle.active
        ? 'VEHICLE_INACTIVE'
        : !company.active || company.walletStatus === 'SUSPENDED'
          ? 'COMPANY_FROZEN'
          : unitPricePiastres === undefined
            ? 'FUEL_NOT_AVAILABLE'
            : limits.some((limit) => limit.remaining <= 0)
              ? 'LIMIT_EXCEEDED'
              : undefined;

  return {
    known: true,
    vehicleId: vehicle.id,
    companyId: company.id,
    plate: vehicle.plate,
    companyNameAr: company.nameAr,
    companyNameEn: company.nameEn,
    fuelType: vehicle.fuelType,
    walletStatus: company.walletStatus,
    billable,
    tankCapacityMl: vehicle.tankCapacityMl,
    unitPricePiastres,
    limits,
    cooldownUntil,
    block,
  };
}

/** The pump's fuel against the vehicle's: a different fuel is refused before any volume is entered. */
export function pumpFuelBlock(check: KnownTag, pumpFuel: FuelType): OfflineDecline | undefined {
  return pumpFuel === check.fuelType ? undefined : 'FUEL_TYPE_MISMATCH';
}

/** What the confirm step shows for a volume, and what it warns about. */
export interface Quote {
  volumeMl: number;
  /** Absent when the terminal has no price (the server prices it). */
  amountPiastres?: number;
  unitPricePiastres?: number;
  billable: boolean;
  /** The volume or its amount is more than a limit has left: the server will refuse it. */
  overLimit?: LimitLeft;
  /** More than the vehicle's tank holds: allowed, but Operations is alerted. */
  overTank: boolean;
}

export function quote(check: TagCheck, volumeMl: number): Quote {
  if (!check.known) return { volumeMl, billable: true, overTank: false };
  const amountPiastres = check.unitPricePiastres === undefined ? undefined : amountForVolume(volumeMl, check.unitPricePiastres);
  const overLimit = check.limits.find((limit) =>
    limit.kind === 'VOLUME' ? volumeMl > limit.remaining : amountPiastres !== undefined && amountPiastres > limit.remaining,
  );
  return {
    volumeMl,
    amountPiastres,
    unitPricePiastres: check.unitPricePiastres,
    billable: check.billable,
    overLimit,
    overTank: check.tankCapacityMl > 0 && volumeMl > check.tankCapacityMl,
  };
}

/** Seconds until an instant, never negative, as "m:ss" or "h:mm:ss" with Western digits. */
export function formatCountdown(untilMs: number, nowMs: number): string {
  const total = Math.max(0, Math.ceil((untilMs - nowMs) / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = String(total % 60).padStart(2, '0');
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${seconds}` : `${minutes}:${seconds}`;
}
