/**
 * The typed API contract used by screens. The demo uses the mock implementation; the production
 * Spring Boot client will implement the same interface, so screens do not change.
 */
import type { FleetSize, LeadStatus } from '@/domain/demo-request';
import type { FuelOutcome, FuelRequest } from '@/domain/fueling';
import type { Company, FuelTransaction, Id, LedgerEntry, Vehicle, Wallet } from '@/domain/types';
import type { Locale } from '@/lib/locale';

export interface TransactionQuery {
  companyId?: Id;
  stationId?: Id;
  vehicleId?: Id;
  limit?: number;
}

/** POST /public/demo-requests: the landing page's "Request a demo" form. No sign-in. */
export interface DemoRequestBody {
  companyName: string;
  contactName: string;
  /** National form, 01 + [0125] + 8 digits (normalised by the client). */
  phone: string;
  email: string;
  fleetSize: FleetSize;
  locale: Locale;
  /** Honeypot: a hidden field people never see. Anything in it marks the request as a bot. */
  website: string;
  /** When the form rendered (ms since the epoch); a form sent too fast is treated as a bot. */
  startedAt: number;
}

export interface DemoRequestReceipt {
  status: 'RECEIVED';
}

/** A demo request as Operations sees it (GET /ops/leads). Platform roles only. */
export interface Lead {
  id: Id;
  /** Short human reference to quote on the phone. */
  reference: string;
  companyName: string;
  contactName: string;
  phone: string;
  email: string;
  fleetSize: FleetSize;
  locale: Locale;
  status: LeadStatus;
  note?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface LeadQuery {
  status?: LeadStatus;
  cursor?: string;
  limit?: number;
}

export interface LeadPage {
  items: Lead[];
  nextCursor?: string | null;
}

export interface LeadUpdate {
  status: LeadStatus;
  note?: string;
}

export interface ZoodApi {
  demoRequests: {
    submit(body: DemoRequestBody): Promise<DemoRequestReceipt>;
  };
  leads: {
    /** Newest first, cursor-paginated like the other Ops lists. */
    list(query: LeadQuery, signal?: AbortSignal): Promise<LeadPage>;
    update(leadId: Id, body: LeadUpdate): Promise<Lead>;
  };
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
