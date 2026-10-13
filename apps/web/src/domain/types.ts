/**
 * Domain types shared by the demo and, later, the API client. They mirror the planned backend DTOs:
 * ids are strings, money is integer piastres, volume is integer milliliters, timestamps are ISO-8601 UTC.
 */
import type { Locale, LocalizedText } from '@/lib/locale';
import type { Milliliters, Piastres } from './money';

export type Id = string;
export type IsoDateTime = string;

export type FuelTypeId = 'gas92' | 'gas95' | 'diesel';

export interface FuelType {
  id: FuelTypeId;
  name: LocalizedText;
}

export type OrganizationStatus = 'active' | 'frozen';

export interface Company {
  id: Id;
  name: LocalizedText;
  status: OrganizationStatus;
  /** Cooling-off window between fuelings of the same tag (PRD: 2–4 hours, per company). */
  cooldownMinutes: number;
  /** Low-balance alert threshold. */
  lowBalanceThresholdPiastres: Piastres;
}

export interface Wallet {
  id: Id;
  companyId: Id;
  /** Cached balance, always equal to the last ledger entry's balanceAfter (reconciled). */
  balancePiastres: Piastres;
}

export type LedgerEntryType = 'topup' | 'fuelDebit' | 'reversal';

/** Immutable wallet ledger entry. Corrections are new compensating entries, never edits. */
export interface LedgerEntry {
  id: Id;
  walletId: Id;
  companyId: Id;
  type: LedgerEntryType;
  /** Signed: credits positive, debits negative. */
  amountPiastres: Piastres;
  balanceAfterPiastres: Piastres;
  transactionId?: Id;
  reference: string;
  at: IsoDateTime;
}

export interface Department {
  id: Id;
  companyId: Id;
  name: LocalizedText;
  monthlyBudgetPiastres: Piastres;
}

export type FleetTier = 'normal' | 'vip';
export type VehicleStatus = 'active' | 'suspended' | 'decommissioned';

export interface Vehicle {
  id: Id;
  companyId: Id;
  departmentId: Id;
  /** Official identifier, shown exactly as registered in every language (never transliterated). */
  plate: string;
  make: LocalizedText;
  model: LocalizedText;
  year: number;
  fuelTypeId: FuelTypeId;
  tankCapacityMl: Milliliters;
  tier: FleetTier;
  status: VehicleStatus;
  tagId: Id;
}

export type TagStatus = 'active' | 'lost' | 'stolen' | 'deactivated' | 'replacementPending';

export interface RfidTag {
  id: Id;
  /** Masked for display; the full tag number is never shown in the UI. */
  maskedUid: string;
  vehicleId: Id;
  status: TagStatus;
  /** Mirrors the cooldown record written on the tag itself (PRD §7.3). */
  cooldownUntil?: IsoDateTime;
}

export type LimitKind = 'volume' | 'amount';
export type LimitPeriod = 'day' | 'week' | 'month';

/** Per-vehicle consumption cap (Normal tier only). Distinct from department budgets. */
export interface LimitRule {
  id: Id;
  companyId: Id;
  vehicleIds: Id[];
  kind: LimitKind;
  period: LimitPeriod;
  /** Milliliters for volume limits, piastres for amount limits. */
  maxValue: number;
}

export type ConnectivityProfile = 'normal' | 'low';

export interface StationOperator {
  id: Id;
  name: LocalizedText;
}

export interface Station {
  id: Id;
  operatorId: Id;
  name: LocalizedText;
  timeZone: string;
  connectivity: ConnectivityProfile;
  /** Current price per liter, set per station by Operations (PRD §6.1). */
  prices: Partial<Record<FuelTypeId, Piastres>>;
}

export type DeviceStatus = 'online' | 'offline' | 'revoked';
export type PrinterStatus = 'ready' | 'paperLow' | 'error';

export interface PosDevice {
  id: Id;
  code: string;
  stationId: Id;
  status: DeviceStatus;
  printer: PrinterStatus;
  lastSyncAt: IsoDateTime;
}

export interface Agent {
  id: Id;
  workerId: string;
  name: LocalizedText;
  stationId: Id;
}

export type TransactionStatus = 'completed' | 'declined' | 'pendingSync' | 'reversed';
export type TransactionOrigin = 'online' | 'offline';
export type DeclineReason =
  | 'cooldown'
  | 'tagInactive'
  | 'vehicleInactive'
  | 'companyFrozen'
  | 'fuelTypeMismatch'
  | 'fuelNotAvailable'
  | 'limitExceeded'
  | 'insufficientBalance';

export interface FuelTransaction {
  id: Id;
  /** Short human reference printed on the receipt, e.g. TX-48213. */
  shortId: string;
  /** Globally unique id generated on the POS before submission; the idempotency key. */
  clientTxnId: Id;
  companyId: Id;
  vehicleId: Id;
  tagId: Id;
  stationId: Id;
  deviceId: Id;
  agentId: Id;
  fuelTypeId: FuelTypeId;
  volumeMl: Milliliters;
  unitPricePiastres: Piastres;
  amountPiastres: Piastres;
  /** False for VIP-tier vehicles: recorded for reporting and settlement, no wallet debit. */
  billable: boolean;
  status: TransactionStatus;
  declineReason?: DeclineReason;
  origin: TransactionOrigin;
  /** Device clock at capture. */
  deviceAt: IsoDateTime;
  /** Server receipt time (absent while pending sync). */
  receivedAt?: IsoDateTime;
  /** Time the offline record was synchronized (offline origin only). */
  syncedAt?: IsoDateTime;
  /** Language of the physical receipt at completion; reprints use it. */
  receiptLocale: Locale;
  ledgerEntryId?: Id;
}

export type AlertType =
  | 'lowBalance'
  | 'limitReached'
  | 'budgetThreshold'
  | 'plateUnverified'
  | 'tankCapacityExceeded'
  | 'syncConflict'
  | 'tagWriteFailure'
  | 'stalePrice'
  | 'vipSafeguard'
  | 'deviceOffline';

export type AlertSeverity = 'info' | 'warning' | 'critical';
export type AlertStatus = 'open' | 'resolved';

export interface Alert {
  id: Id;
  type: AlertType;
  severity: AlertSeverity;
  status: AlertStatus;
  at: IsoDateTime;
  companyId?: Id;
  vehicleId?: Id;
  stationId?: Id;
  deviceId?: Id;
  transactionId?: Id;
}
