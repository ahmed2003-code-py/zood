import type { Dataset } from '@/domain/dataset';
import type { Milliliters, Piastres } from '@/domain/money';
import type { QueuedFuel } from '@/domain/pos';
import type { FuelTransaction } from '@/domain/types';
import type { Locale, LocalizedText } from '@/lib/locale';

/**
 * Everything printed on the compact receipt, and nothing more (no wallet balance, limits, tag or
 * device identifiers, sync or audit data). The language is fixed at completion (receiptLocale), so
 * a reprint matches the original even if the POS language changed since.
 */
export interface ReceiptModel {
  reference: string;
  /** Opaque link target for the QR code (the digital receipt). */
  clientTxnId: string;
  stationName: LocalizedText;
  timeZone: string;
  at: string;
  plate: string;
  fuelName: LocalizedText;
  volumeMl: Milliliters;
  unitPricePiastres: Piastres;
  amountPiastres: Piastres;
  billable: boolean;
  locale: Locale;
  pendingSync: boolean;
}

export function receiptFromTransaction(data: Dataset, txn: FuelTransaction): ReceiptModel {
  const station = data.stations[txn.stationId];
  return {
    reference: txn.shortId,
    clientTxnId: txn.clientTxnId,
    stationName: station?.name ?? { ar: '', en: '' },
    timeZone: station?.timeZone ?? 'Africa/Cairo',
    at: txn.deviceAt,
    plate: data.vehicles[txn.vehicleId]?.plate ?? '',
    fuelName: data.fuelTypes[txn.fuelTypeId]?.name ?? { ar: '', en: '' },
    volumeMl: txn.volumeMl,
    unitPricePiastres: txn.unitPricePiastres,
    amountPiastres: txn.amountPiastres,
    billable: txn.billable,
    locale: txn.receiptLocale,
    pendingSync: false,
  };
}

export function receiptFromQueued(data: Dataset, item: QueuedFuel): ReceiptModel {
  const tag = data.tags[item.request.tagId];
  const vehicle = tag ? data.vehicles[tag.vehicleId] : undefined;
  const station = data.stations[item.request.stationId];
  return {
    reference: item.request.shortId ?? item.request.clientTxnId.slice(0, 8).toUpperCase(),
    clientTxnId: item.request.clientTxnId,
    stationName: station?.name ?? { ar: '', en: '' },
    timeZone: station?.timeZone ?? 'Africa/Cairo',
    at: item.request.deviceAt,
    plate: vehicle?.plate ?? '',
    fuelName: vehicle ? data.fuelTypes[vehicle.fuelTypeId].name : { ar: '', en: '' },
    volumeMl: item.request.volumeMl,
    unitPricePiastres: item.unitPricePiastres,
    amountPiastres: item.amountPiastres,
    billable: vehicle?.tier !== 'vip',
    locale: item.request.receiptLocale,
    pendingSync: true,
  };
}

/** Digital receipt route for a transaction (QR target and "view receipt"). */
export function digitalReceiptPath(clientTxnId: string): string {
  return `/receipt/${clientTxnId}`;
}
