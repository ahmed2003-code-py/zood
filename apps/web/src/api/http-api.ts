/**
 * ZoodApi over HTTP: the implementation screens use when VITE_API_MODE=http.
 *
 * The backend is built in phases (docs/architecture/implementation-plan.md §7). A group below is
 * connected in the phase that builds its endpoints; until then it fails with a clear error instead
 * of pretending, so a screen shows its error state and nothing is silently faked.
 */
import type { ZoodApi } from './types';

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

export const httpApi: ZoodApi = {
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
