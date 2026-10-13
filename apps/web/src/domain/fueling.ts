/**
 * Fueling transaction engine (demo implementation of the production Transaction Engine).
 *
 * One call is one atomic unit: the transaction record, wallet ledger entry, wallet balance,
 * tag cooldown and alerts are all produced together from the same input, or not at all.
 * Replaying the same clientTxnId returns the original outcome without any new effect (idempotency).
 */
import type { Locale } from '@/lib/locale';
import { isSamePeriod } from './calendar';
import type { Dataset } from './dataset';
import { amountForVolume, type Milliliters } from './money';
import type {
  Alert,
  DeclineReason,
  FuelTransaction,
  FuelTypeId,
  Id,
  IsoDateTime,
  LedgerEntry,
  TransactionOrigin,
  Vehicle,
} from './types';

export interface FuelRequest {
  clientTxnId: Id;
  tagId: Id;
  stationId: Id;
  deviceId: Id;
  agentId: Id;
  volumeMl: Milliliters;
  /** Optional explicit fuel selection; must match the vehicle's fuel type. */
  fuelTypeId?: FuelTypeId;
  /** online: authorized now. offline: captured on the device earlier and ingested at sync. */
  origin: TransactionOrigin;
  deviceAt: IsoDateTime;
  receiptLocale: Locale;
  /** Receipt reference assigned on the device (offline captures print it before the server sees them). */
  shortId?: string;
}

export interface FuelOutcome {
  transaction: FuelTransaction;
  /** True when this clientTxnId was already processed; nothing changed. */
  replayed: boolean;
}

export interface IdFactory {
  next(): Id;
}

export const randomIds: IdFactory = { next: () => crypto.randomUUID() };

export class FuelingError extends Error {}

/**
 * Validation shared by the server engine and the POS (online pre-check and offline cached check).
 * Order follows PRD §6.1: tag cooldown first, then tag, vehicle, company, fuel, limits, wallet.
 */
export function checkFueling(
  data: Dataset,
  request: FuelRequest,
  vehicle: Vehicle,
  amountPiastres: number,
  now: Date,
): DeclineReason | undefined {
  const tag = data.tags[request.tagId];
  const company = data.companies[vehicle.companyId];
  const station = data.stations[request.stationId];
  if (!tag || !company || !station) throw new FuelingError('Unknown tag, company or station');

  // PRD §6.1: the tag's cooldown is checked first, before any other validation.
  if (tag.cooldownUntil && new Date(tag.cooldownUntil) > now) return 'cooldown';
  if (tag.status !== 'active') return 'tagInactive';
  if (vehicle.status !== 'active') return 'vehicleInactive';
  if (company.status !== 'active') return 'companyFrozen';
  if (request.fuelTypeId && request.fuelTypeId !== vehicle.fuelTypeId) return 'fuelTypeMismatch';
  if (station.prices[vehicle.fuelTypeId] === undefined) return 'fuelNotAvailable';

  // VIP tier skips wallet and limit checks (PRD §4.4).
  if (vehicle.tier === 'vip') return undefined;

  for (const rule of data.limitRules) {
    if (!rule.vehicleIds.includes(vehicle.id)) continue;
    const used = data.transactions
      .filter(
        (txn) =>
          txn.vehicleId === vehicle.id &&
          txn.status === 'completed' &&
          isSamePeriod(new Date(txn.deviceAt), now, rule.period, station.timeZone),
      )
      .reduce((sum, txn) => sum + (rule.kind === 'volume' ? txn.volumeMl : txn.amountPiastres), 0);
    const requested = rule.kind === 'volume' ? request.volumeMl : amountPiastres;
    if (used + requested > rule.maxValue) return 'limitExceeded';
  }

  const wallet = data.wallets[company.id];
  if (!wallet) throw new FuelingError('Company has no wallet');
  if (wallet.balancePiastres - amountPiastres < 0) return 'insufficientBalance';
  return undefined;
}

export function recordFuelTransaction(
  data: Dataset,
  request: FuelRequest,
  now: Date,
  ids: IdFactory = randomIds,
): { data: Dataset; outcome: FuelOutcome } {
  const existing = data.transactions.find((txn) => txn.clientTxnId === request.clientTxnId);
  if (existing) return { data, outcome: { transaction: existing, replayed: true } };

  if (!Number.isSafeInteger(request.volumeMl) || request.volumeMl <= 0) {
    throw new FuelingError('Volume must be a positive integer number of milliliters');
  }

  const tag = data.tags[request.tagId];
  const vehicle = tag ? data.vehicles[tag.vehicleId] : undefined;
  const station = data.stations[request.stationId];
  if (!tag || !vehicle || !station) throw new FuelingError('Unknown tag or station');

  const unitPricePiastres = station.prices[vehicle.fuelTypeId] ?? 0;
  const amountPiastres = amountForVolume(request.volumeMl, unitPricePiastres);
  const nowIso = now.toISOString();
  const sequence = data.sequence + 1;
  const transactionId = ids.next();

  const isOffline = request.origin === 'offline';
  // Online requests are evaluated at server time; offline ones at their device capture time.
  const evaluatedAt = isOffline ? new Date(request.deviceAt) : now;
  const reason = checkFueling(data, request, vehicle, amountPiastres, evaluatedAt);
  // Offline transactions were already approved on the device against its cached snapshot.
  // At sync they are committed, never reversed against the agent or driver; conflicts become alerts (PRD §7.5).
  const declined = reason !== undefined && !isOffline;

  const transaction: FuelTransaction = {
    id: transactionId,
    shortId: request.shortId ?? `TX-${sequence}`,
    clientTxnId: request.clientTxnId,
    companyId: vehicle.companyId,
    vehicleId: vehicle.id,
    tagId: tag.id,
    stationId: station.id,
    deviceId: request.deviceId,
    agentId: request.agentId,
    fuelTypeId: vehicle.fuelTypeId,
    volumeMl: request.volumeMl,
    unitPricePiastres,
    amountPiastres,
    billable: vehicle.tier === 'normal',
    status: declined ? 'declined' : 'completed',
    declineReason: declined ? reason : undefined,
    origin: request.origin,
    deviceAt: request.deviceAt,
    receivedAt: nowIso,
    syncedAt: isOffline ? nowIso : undefined,
    receiptLocale: request.receiptLocale,
  };

  if (declined) {
    return {
      data: { ...data, sequence, transactions: [...data.transactions, transaction] },
      outcome: { transaction, replayed: false },
    };
  }

  let wallets = data.wallets;
  let ledger = data.ledger;
  if (transaction.billable) {
    const wallet = data.wallets[vehicle.companyId];
    if (!wallet) throw new FuelingError('Company has no wallet');
    const balanceAfterPiastres = wallet.balancePiastres - amountPiastres;
    const entry: LedgerEntry = {
      id: ids.next(),
      walletId: wallet.id,
      companyId: wallet.companyId,
      type: 'fuelDebit',
      amountPiastres: -amountPiastres,
      balanceAfterPiastres,
      transactionId,
      reference: transaction.shortId,
      at: nowIso,
    };
    transaction.ledgerEntryId = entry.id;
    wallets = { ...wallets, [wallet.companyId]: { ...wallet, balancePiastres: balanceAfterPiastres } };
    ledger = [...ledger, entry];
  }

  const company = data.companies[vehicle.companyId];
  const cooldownUntil = new Date(evaluatedAt.getTime() + (company?.cooldownMinutes ?? 0) * 60_000);
  const tags = { ...data.tags, [tag.id]: { ...tag, cooldownUntil: cooldownUntil.toISOString() } };

  const alerts: Alert[] = [];
  if (reason !== undefined && isOffline) {
    alerts.push({
      id: ids.next(),
      type: 'syncConflict',
      severity: 'warning',
      status: 'open',
      at: nowIso,
      companyId: vehicle.companyId,
      vehicleId: vehicle.id,
      stationId: station.id,
      deviceId: request.deviceId,
      transactionId,
    });
  }
  if (request.volumeMl > vehicle.tankCapacityMl) {
    // PRD §10: flagged to Operations, never blocking.
    alerts.push({
      id: ids.next(),
      type: 'tankCapacityExceeded',
      severity: 'critical',
      status: 'open',
      at: nowIso,
      companyId: vehicle.companyId,
      vehicleId: vehicle.id,
      stationId: station.id,
      deviceId: request.deviceId,
      transactionId,
    });
  }

  return {
    data: {
      ...data,
      sequence,
      wallets,
      ledger,
      tags,
      transactions: [...data.transactions, transaction],
      alerts: alerts.length ? [...data.alerts, ...alerts] : data.alerts,
    },
    outcome: { transaction, replayed: false },
  };
}
