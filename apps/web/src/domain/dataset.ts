import type {
  Agent,
  Alert,
  Company,
  Department,
  FuelTransaction,
  FuelType,
  FuelTypeId,
  Id,
  LedgerEntry,
  LimitRule,
  PosDevice,
  RfidTag,
  Station,
  StationOperator,
  Vehicle,
  Wallet,
} from './types';

/**
 * The full domain state held by the demo's mock backend. In production this lives in PostgreSQL;
 * the shape is normalized the same way (records keyed by id, append-only ledger and transactions).
 */
export interface Dataset {
  fuelTypes: Record<FuelTypeId, FuelType>;
  companies: Record<Id, Company>;
  /** Keyed by company id (one prepaid wallet per company, PRD §4.1). */
  wallets: Record<Id, Wallet>;
  /** Append-only, oldest first. */
  ledger: LedgerEntry[];
  departments: Record<Id, Department>;
  vehicles: Record<Id, Vehicle>;
  tags: Record<Id, RfidTag>;
  limitRules: LimitRule[];
  operators: Record<Id, StationOperator>;
  stations: Record<Id, Station>;
  devices: Record<Id, PosDevice>;
  agents: Record<Id, Agent>;
  /** Append-only, oldest first. */
  transactions: FuelTransaction[];
  alerts: Alert[];
  /** Monotonic counter used for short human-readable references. */
  sequence: number;
}
