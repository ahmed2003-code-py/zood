/**
 * Read models over the demo dataset (pure functions). They mirror the summaries the backend will
 * expose; screens call them through useMemo so the store snapshot itself stays referentially stable.
 */
import { isSamePeriod, periodKey } from '@/domain/calendar';
import type { Dataset } from '@/domain/dataset';
import type { Alert, FuelTransaction, Id } from '@/domain/types';
import { DEFAULT_TIME_ZONE } from '@/lib/format';

const newestFirst = (transactions: FuelTransaction[]) => [...transactions].reverse();

export function isToday(iso: string, now: Date, timeZone = DEFAULT_TIME_ZONE): boolean {
  return periodKey(new Date(iso), 'day', timeZone) === periodKey(now, 'day', timeZone);
}

export function recentTransactions(data: Dataset, filter: { companyId?: Id; stationId?: Id }, limit: number) {
  return newestFirst(
    data.transactions.filter(
      (txn) => (!filter.companyId || txn.companyId === filter.companyId) && (!filter.stationId || txn.stationId === filter.stationId),
    ),
  ).slice(0, limit);
}

export function companySummary(data: Dataset, companyId: Id, now: Date) {
  const completedToday = data.transactions.filter(
    (txn) => txn.companyId === companyId && txn.status === 'completed' && isToday(txn.deviceAt, now),
  );
  const vehicles = Object.values(data.vehicles).filter((vehicle) => vehicle.companyId === companyId);
  const active = vehicles.filter((vehicle) => vehicle.status === 'active');
  const lastTopup = [...data.ledger].reverse().find((entry) => entry.companyId === companyId && entry.type === 'topup');
  return {
    balancePiastres: data.wallets[companyId]?.balancePiastres ?? 0,
    todaySpendPiastres: completedToday.filter((txn) => txn.billable).reduce((sum, txn) => sum + txn.amountPiastres, 0),
    todayTransactions: completedToday.length,
    vehicleCount: vehicles.length,
    activeVehicles: active.length,
    activeNormal: active.filter((vehicle) => vehicle.tier === 'normal').length,
    activeVip: active.filter((vehicle) => vehicle.tier === 'vip').length,
    suspendedVehicles: vehicles.filter((vehicle) => vehicle.status === 'suspended').length,
    lastTopup,
  };
}

/** Spend this month per department (completed, billable transactions). */
export function departmentBudgets(data: Dataset, companyId: Id, now: Date) {
  return Object.values(data.departments)
    .filter((department) => department.companyId === companyId)
    .map((department) => {
      const spent = data.transactions
        .filter(
          (txn) =>
            txn.status === 'completed' &&
            txn.billable &&
            data.vehicles[txn.vehicleId]?.departmentId === department.id &&
            isSamePeriod(new Date(txn.deviceAt), now, 'month', DEFAULT_TIME_ZONE),
        )
        .reduce((sum, txn) => sum + txn.amountPiastres, 0);
      return { department, spentPiastres: spent };
    });
}

export function lastFueling(data: Dataset, vehicleId: Id): FuelTransaction | undefined {
  for (let index = data.transactions.length - 1; index >= 0; index--) {
    const txn = data.transactions[index];
    if (txn?.vehicleId === vehicleId && txn.status === 'completed') return txn;
  }
  return undefined;
}

export function openAlerts(data: Dataset, filter: { companyId?: Id; stationId?: Id } = {}): Alert[] {
  return [...data.alerts]
    .filter(
      (alert) =>
        alert.status === 'open' &&
        (!filter.companyId || alert.companyId === filter.companyId) &&
        (!filter.stationId || alert.stationId === filter.stationId),
    )
    .sort((a, b) => b.at.localeCompare(a.at));
}

export function stationSummary(data: Dataset, stationId: Id, now: Date) {
  const today = data.transactions.filter((txn) => txn.stationId === stationId && txn.status === 'completed' && isToday(txn.deviceAt, now));
  const devices = Object.values(data.devices).filter((device) => device.stationId === stationId);
  return {
    valueTodayPiastres: today.reduce((sum, txn) => sum + txn.amountPiastres, 0),
    litersTodayMl: today.reduce((sum, txn) => sum + txn.volumeMl, 0),
    transactionsToday: today.length,
    devices,
    devicesOnline: devices.filter((device) => device.status === 'online').length,
    pendingSync: data.transactions.filter((txn) => txn.stationId === stationId && txn.status === 'pendingSync').length,
  };
}

/** Fuel dispensed at a station this month, including non-billable (VIP) volume (PRD §4.4). */
export function stationSettlement(data: Dataset, stationId: Id, now: Date) {
  const month = data.transactions.filter(
    (txn) => txn.stationId === stationId && txn.status === 'completed' && isSamePeriod(new Date(txn.deviceAt), now, 'month', DEFAULT_TIME_ZONE),
  );
  return {
    volumeMl: month.reduce((sum, txn) => sum + txn.volumeMl, 0),
    amountPiastres: month.reduce((sum, txn) => sum + txn.amountPiastres, 0),
    vipVolumeMl: month.filter((txn) => !txn.billable).reduce((sum, txn) => sum + txn.volumeMl, 0),
    transactions: month.length,
  };
}

export function agentActivityToday(data: Dataset, stationId: Id, now: Date) {
  return Object.values(data.agents)
    .filter((agent) => agent.stationId === stationId)
    .map((agent) => {
      const txns = data.transactions.filter((txn) => txn.agentId === agent.id && txn.status === 'completed' && isToday(txn.deviceAt, now));
      const first = txns[0];
      return {
        agent,
        transactions: txns.length,
        volumeMl: txns.reduce((sum, txn) => sum + txn.volumeMl, 0),
        valuePiastres: txns.reduce((sum, txn) => sum + txn.amountPiastres, 0),
        startedAt: first?.deviceAt,
      };
    });
}

export function platformSummary(data: Dataset, now: Date) {
  const today = data.transactions.filter((txn) => txn.status === 'completed' && isToday(txn.deviceAt, now));
  const devices = Object.values(data.devices);
  const reconciled = Object.values(data.wallets).every((wallet) => {
    const entries = data.ledger.filter((entry) => entry.companyId === wallet.companyId);
    return entries.reduce((sum, entry) => sum + entry.amountPiastres, 0) === wallet.balancePiastres;
  });
  return {
    volumeTodayPiastres: today.reduce((sum, txn) => sum + txn.amountPiastres, 0),
    activeCompanies: Object.values(data.companies).filter((company) => company.status === 'active').length,
    activeStations: Object.values(data.stations).length,
    devicesOffline: devices.filter((device) => device.status === 'offline').length,
    openAlerts: data.alerts.filter((alert) => alert.status === 'open').length,
    reconciled,
  };
}

export function monthSpend(data: Dataset, companyId: Id, now: Date): number {
  return data.transactions
    .filter(
      (txn) =>
        txn.companyId === companyId &&
        txn.status === 'completed' &&
        txn.billable &&
        isSamePeriod(new Date(txn.deviceAt), now, 'month', DEFAULT_TIME_ZONE),
    )
    .reduce((sum, txn) => sum + txn.amountPiastres, 0);
}

/** Spend per local day for the last `days` days, oldest first (for charts). */
export function dailySpend(data: Dataset, companyId: Id, now: Date, days: number) {
  const buckets = Array.from({ length: days }, (_, index) => {
    const at = new Date(now.getTime() - (days - 1 - index) * 86_400_000);
    return { key: periodKey(at, 'day', DEFAULT_TIME_ZONE), at, amountPiastres: 0 };
  });
  const byKey = new Map(buckets.map((bucket) => [bucket.key, bucket]));
  for (const txn of data.transactions) {
    if (txn.companyId !== companyId || txn.status !== 'completed' || !txn.billable) continue;
    const bucket = byKey.get(periodKey(new Date(txn.deviceAt), 'day', DEFAULT_TIME_ZONE));
    if (bucket) bucket.amountPiastres += txn.amountPiastres;
  }
  return buckets;
}

/** Usage against the vehicle's strictest volume rule today, plus this month's totals. */
export function vehicleUsage(data: Dataset, vehicleId: Id, now: Date) {
  const vehicleTxns = data.transactions.filter((txn) => txn.vehicleId === vehicleId && txn.status === 'completed');
  const rule = data.limitRules.find((candidate) => candidate.vehicleIds.includes(vehicleId));
  const usedInPeriod = rule
    ? vehicleTxns
        .filter((txn) => isSamePeriod(new Date(txn.deviceAt), now, rule.period, DEFAULT_TIME_ZONE))
        .reduce((sum, txn) => sum + (rule.kind === 'volume' ? txn.volumeMl : txn.amountPiastres), 0)
    : 0;
  const month = vehicleTxns.filter((txn) => isSamePeriod(new Date(txn.deviceAt), now, 'month', DEFAULT_TIME_ZONE));
  return {
    rule,
    usedInPeriod,
    monthVolumeMl: month.reduce((sum, txn) => sum + txn.volumeMl, 0),
    monthSpendPiastres: month.filter((txn) => txn.billable).reduce((sum, txn) => sum + txn.amountPiastres, 0),
    recent: [...vehicleTxns].reverse().slice(0, 5),
  };
}

export type PeriodFilter = 'today' | 'week' | 'month' | 'all';

export interface TransactionFilter {
  companyId?: Id;
  stationId?: Id;
  period: PeriodFilter;
  status?: FuelTransaction['status'];
  vehicleId?: Id;
  filterStationId?: Id;
}

export function filterTransactions(data: Dataset, filter: TransactionFilter, now: Date): FuelTransaction[] {
  const since =
    filter.period === 'week' ? now.getTime() - 7 * 86_400_000 : filter.period === 'month' ? now.getTime() - 30 * 86_400_000 : undefined;
  return newestFirst(
    data.transactions.filter(
      (txn) =>
        (!filter.companyId || txn.companyId === filter.companyId) &&
        (!filter.stationId || txn.stationId === filter.stationId) &&
        (!filter.status || txn.status === filter.status) &&
        (!filter.vehicleId || txn.vehicleId === filter.vehicleId) &&
        (!filter.filterStationId || txn.stationId === filter.filterStationId) &&
        (filter.period !== 'today' || isToday(txn.deviceAt, now)) &&
        (since === undefined || new Date(txn.deviceAt).getTime() >= since),
    ),
  );
}
