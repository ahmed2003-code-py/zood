/**
 * ZoodApi over HTTP: the implementation screens use when VITE_API_MODE=http.
 *
 * The backend is built in phases (docs/architecture/implementation-plan.md §7). A group below is
 * connected in the phase that builds its endpoints; until then it fails with a clear error instead
 * of pretending, so a screen shows its error state and nothing is silently faked.
 */
import type { ApiRequestInit } from './http-client';
import type { DemoRequestReceipt, Lead, LeadPage, LeadQuery, ZoodApi } from './types';

/** A ZoodApi method whose endpoint does not exist on the server yet. */
export class EndpointNotAvailableError extends Error {
  readonly method: string;
  readonly phase: string;

  constructor(method: string, phase: string) {
    super(`${method} is not available over HTTP yet: its endpoint is built in phase ${phase}`);
    this.name = 'EndpointNotAvailableError';
    this.method = method;
    this.phase = phase;
  }
}

const pending = (method: string, phase: string) => (): Promise<never> =>
  Promise.reject(new EndpointNotAvailableError(method, phase));

/**
 * The HTTP client loads on first use, so it stays out of the entry chunk (the landing page and the
 * portals fetch it with their own chunks).
 */
const request = <T>(path: string, init?: ApiRequestInit): Promise<T> =>
  import('./http-client').then((client) => client.apiRequest<T>(path, init));

function leadQuery({ status, cursor, limit }: LeadQuery): string {
  const search = new URLSearchParams();
  if (status) search.set('status', status);
  if (cursor) search.set('cursor', cursor);
  if (limit) search.set('limit', String(limit));
  const text = search.toString();
  return text ? `?${text}` : '';
}

export const httpApi: ZoodApi = {
  demoRequests: {
    // Public: no token, and a 401 never triggers a session refresh.
    submit: (body) => request<DemoRequestReceipt>('/public/demo-requests', { method: 'POST', json: body, anonymous: true }),
  },
  leads: {
    list: (query, signal) => request<LeadPage>(`/ops/leads${leadQuery(query)}`, { signal }),
    update: (leadId, body) => request<Lead>(`/ops/leads/${encodeURIComponent(leadId)}`, { method: 'PATCH', json: body }),
  },
  companies: {
    get: pending('companies.get', 'B2'),
  },
  wallet: {
    get: pending('wallet.get', 'B3'),
    ledger: pending('wallet.ledger', 'B3'),
  },
  vehicles: {
    list: pending('vehicles.list', 'B2'),
    get: pending('vehicles.get', 'B2'),
  },
  transactions: {
    list: pending('transactions.list', 'B4'),
    get: pending('transactions.get', 'B4'),
  },
  fueling: {
    record: pending('fueling.record', 'B4'),
  },
};
