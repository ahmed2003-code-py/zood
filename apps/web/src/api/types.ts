/**
 * The typed API contract used by screens. The demo uses the mock implementation; the production
 * Spring Boot client will implement the same interface, so screens do not change.
 */
import type { FuelOutcome, FuelRequest } from '@/domain/fueling';
import type { Company, FuelTransaction, Id, LedgerEntry, Vehicle, Wallet } from '@/domain/types';

export interface TransactionQuery {
  companyId?: Id;
  stationId?: Id;
  vehicleId?: Id;
  limit?: number;
}

export interface ZoodApi {
  companies: {
    get(companyId: Id): Promise<Company>;
  };
  wallet: {
    get(companyId: Id): Promise<Wallet>;
    ledger(companyId: Id, limit?: number): Promise<LedgerEntry[]>;
  };
  vehicles: {
    list(companyId: Id): Promise<Vehicle[]>;
    get(vehicleId: Id): Promise<Vehicle>;
  };
  transactions: {
    /** Newest first. */
    list(query: TransactionQuery): Promise<FuelTransaction[]>;
    get(transactionId: Id): Promise<FuelTransaction>;
  };
  fueling: {
    /** Idempotent on request.clientTxnId. */
    record(request: FuelRequest): Promise<FuelOutcome>;
  };
}

export class NotFoundError extends Error {
  constructor(entity: string, id: Id) {
    super(`${entity} ${id} not found`);
  }
}
