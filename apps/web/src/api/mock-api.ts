import { useDemoStore } from '@/mocks/store';
import { NotFoundError, type ZoodApi } from './types';

/** Simulated network latency so loading states are exercised in the demo. */
const LATENCY_MS = 150;

function respond<T>(produce: () => T): Promise<T> {
  return new Promise((resolve, reject) => {
    setTimeout(() => {
      try {
        resolve(produce());
      } catch (error) {
        reject(error);
      }
    }, LATENCY_MS);
  });
}

const data = () => useDemoStore.getState().data;

function required<T>(value: T | undefined, entity: string, id: string): T {
  if (value === undefined) throw new NotFoundError(entity, id);
  return value;
}

export const mockApi: ZoodApi = {
  companies: {
    get: (companyId) => respond(() => required(data().companies[companyId], 'Company', companyId)),
  },
  wallet: {
    get: (companyId) => respond(() => required(data().wallets[companyId], 'Wallet', companyId)),
    ledger: (companyId, limit = 50) =>
      respond(() =>
        data()
          .ledger.filter((entry) => entry.companyId === companyId)
          .slice(-limit)
          .reverse(),
      ),
  },
  vehicles: {
    list: (companyId) => respond(() => Object.values(data().vehicles).filter((vehicle) => vehicle.companyId === companyId)),
    get: (vehicleId) => respond(() => required(data().vehicles[vehicleId], 'Vehicle', vehicleId)),
  },
  transactions: {
    list: ({ companyId, stationId, vehicleId, limit = 50 }) =>
      respond(() =>
        data()
          .transactions.filter(
            (txn) =>
              (!companyId || txn.companyId === companyId) &&
              (!stationId || txn.stationId === stationId) &&
              (!vehicleId || txn.vehicleId === vehicleId),
          )
          .slice(-limit)
          .reverse(),
      ),
    get: (transactionId) =>
      respond(() => required(data().transactions.find((txn) => txn.id === transactionId), 'Transaction', transactionId)),
  },
  fueling: {
    record: (request) => respond(() => useDemoStore.getState().recordFuel(request)),
  },
};
