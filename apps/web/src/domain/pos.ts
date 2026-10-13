/**
 * Point-of-sale decision logic (pure). The POS uses it to validate a scanned tag before fuel entry,
 * and, when offline, to validate against its cached snapshot plus its own not-yet-synced captures
 * (PRD §7.1, §7.4: provisional local decrement so offline captures cannot overspend).
 */
import { isSamePeriod } from './calendar';
import type { Dataset } from './dataset';
import { checkFueling, recordFuelTransaction, type FuelRequest } from './fueling';
import { divRoundHalfUp, type Milliliters, type Piastres } from './money';
import type { DeclineReason, Id, IsoDateTime, RfidTag, Vehicle } from './types';

export type WalletStatus = 'available' | 'low' | 'insufficient' | 'suspended';

/** Wallet status shown to the agent as a word only; the amount is never shown on the POS (PRD §4.7). */
export function walletStatus(data: Dataset, companyId: Id): WalletStatus {
  const company = data.companies[companyId];
  const balance = data.wallets[companyId]?.balancePiastres ?? 0;
  if (!company || company.status !== 'active') return 'suspended';
  if (balance <= 0) return 'insufficient';
  if (balance < company.lowBalanceThresholdPiastres) return 'low';
  return 'available';
}

/** A captured transaction waiting on the device for synchronization. */
export interface QueuedFuel {
  request: FuelRequest;
  /** Display values computed at capture with the cached price (the server recomputes at sync). */
  amountPiastres: Piastres;
  unitPricePiastres: Piastres;
  capturedAt: IsoDateTime;
}

/** The device's view of the world: cached server data with its own pending captures applied. */
export function provisionalSnapshot(data: Dataset, queue: QueuedFuel[]): Dataset {
  let snapshot = data;
  for (const item of queue) {
    snapshot = recordFuelTransaction(snapshot, { ...item.request, origin: 'online' }, new Date(item.request.deviceAt)).data;
  }
  return snapshot;
}

export interface ScanContext {
  tagId: Id;
  stationId: Id;
  now: Date;
  /** Cooldown record read from the physical tag (written by any POS, online or offline). */
  tagCooldownUntil?: IsoDateTime;
}

export type ScanResult =
  | {
      ok: true;
      vehicle: Vehicle;
      tag: RfidTag;
      unitPricePiastres: Piastres;
      /** Normal tier only. */
      walletStatus?: WalletStatus;
      /** Volume still allowed by the strictest limit rule today/this period; undefined when unlimited. */
      remainingMl?: Milliliters;
    }
  | { ok: false; reason: DeclineReason | 'unknownTag'; vehicle?: Vehicle; cooldownUntil?: IsoDateTime };

/** Validates a scanned tag before fuel entry. Amount-dependent checks happen again at confirmation. */
export function evaluateScan(data: Dataset, context: ScanContext): ScanResult {
  const tag = data.tags[context.tagId];
  const vehicle = tag ? data.vehicles[tag.vehicleId] : undefined;
  const station = data.stations[context.stationId];
  if (!tag || !vehicle || !station) return { ok: false, reason: 'unknownTag' };

  // The tag's own record is read first (PRD §7.3), then the central mirror.
  const cooldowns = [context.tagCooldownUntil, tag.cooldownUntil].filter((value): value is string => Boolean(value));
  const activeCooldown = cooldowns.filter((value) => new Date(value) > context.now).sort().at(-1);
  if (activeCooldown) return { ok: false, reason: 'cooldown', vehicle, cooldownUntil: activeCooldown };

  const probe: FuelRequest = {
    clientTxnId: 'scan-probe',
    tagId: tag.id,
    stationId: station.id,
    deviceId: 'scan-probe',
    agentId: 'scan-probe',
    volumeMl: 1,
    origin: 'online',
    deviceAt: context.now.toISOString(),
    receiptLocale: 'ar',
  };
  const blocked = checkFueling({ ...data, tags: { ...data.tags, [tag.id]: { ...tag, cooldownUntil: undefined } } }, probe, vehicle, 0, context.now);
  if (blocked && blocked !== 'limitExceeded' && blocked !== 'insufficientBalance') return { ok: false, reason: blocked, vehicle };

  const unitPricePiastres = station.prices[vehicle.fuelTypeId] ?? 0;
  if (vehicle.tier === 'vip') return { ok: true, vehicle, tag, unitPricePiastres };

  const status = walletStatus(data, vehicle.companyId);
  if (status === 'insufficient') return { ok: false, reason: 'insufficientBalance', vehicle };

  let remainingMl: Milliliters | undefined;
  for (const rule of data.limitRules) {
    if (!rule.vehicleIds.includes(vehicle.id)) continue;
    const used = data.transactions
      .filter(
        (txn) =>
          txn.vehicleId === vehicle.id &&
          txn.status === 'completed' &&
          isSamePeriod(new Date(txn.deviceAt), context.now, rule.period, station.timeZone),
      )
      .reduce((sum, txn) => sum + (rule.kind === 'volume' ? txn.volumeMl : txn.amountPiastres), 0);
    const left = Math.max(0, rule.maxValue - used);
    const leftMl = rule.kind === 'volume' ? left : unitPricePiastres > 0 ? divRoundHalfUp(left * 1000, unitPricePiastres) : 0;
    remainingMl = remainingMl === undefined ? leftMl : Math.min(remainingMl, leftMl);
  }
  if (remainingMl !== undefined && remainingMl <= 0) return { ok: false, reason: 'limitExceeded', vehicle };

  return { ok: true, vehicle, tag, unitPricePiastres, walletStatus: status, remainingMl };
}

/** Offline confirmation: the full check against the cached snapshot with pending captures applied. */
export function validateOffline(snapshot: Dataset, request: FuelRequest, amountPiastres: Piastres, now: Date): DeclineReason | undefined {
  const tag = snapshot.tags[request.tagId];
  const vehicle = tag ? snapshot.vehicles[tag.vehicleId] : undefined;
  if (!tag || !vehicle) return 'tagInactive';
  // Cooldown was already enforced from the tag record at scan time.
  const withoutCooldown = { ...snapshot, tags: { ...snapshot.tags, [tag.id]: { ...tag, cooldownUntil: undefined } } };
  return checkFueling(withoutCooldown, request, vehicle, amountPiastres, now);
}
