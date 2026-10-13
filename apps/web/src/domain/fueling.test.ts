import { describe, expect, it } from 'vitest';
import { createSeedDataset, DEMO_IDS } from '@/mocks/seed';
import type { Dataset } from './dataset';
import { recordFuelTransaction, type FuelRequest } from './fueling';

const NOW = new Date('2026-10-12T12:00:00Z'); // 15:00 in Cairo

function request(overrides: Partial<FuelRequest> = {}): FuelRequest {
  return {
    clientTxnId: 'client-1',
    tagId: `tag-${DEMO_IDS.vehicleCorolla}`,
    stationId: DEMO_IDS.station,
    deviceId: DEMO_IDS.device,
    agentId: DEMO_IDS.agent,
    volumeMl: 40_000,
    origin: 'online',
    deviceAt: NOW.toISOString(),
    receiptLocale: 'ar',
    ...overrides,
  };
}

function walletBalance(data: Dataset) {
  return data.wallets[DEMO_IDS.company]?.balancePiastres ?? NaN;
}

function ledgerIsConsistent(data: Dataset, companyId: string) {
  let balance = 0;
  for (const entry of data.ledger.filter((item) => item.companyId === companyId)) {
    balance += entry.amountPiastres;
    if (entry.balanceAfterPiastres !== balance) return false;
  }
  return balance === data.wallets[companyId]?.balancePiastres;
}

describe('seed dataset', () => {
  const seed = createSeedDataset(NOW);

  it('starts the hero company at the demo balance with a consistent ledger', () => {
    expect(walletBalance(seed)).toBe(84_235_000);
    expect(ledgerIsConsistent(seed, DEMO_IDS.company)).toBe(true);
  });

  it('has 47 vehicles for the hero company', () => {
    expect(Object.values(seed.vehicles).filter((vehicle) => vehicle.companyId === DEMO_IDS.company)).toHaveLength(47);
  });
});

describe('recordFuelTransaction', () => {
  it('debits the wallet, writes one ledger entry and starts the tag cooldown atomically', () => {
    const seed = createSeedDataset(NOW);
    const { data, outcome } = recordFuelTransaction(seed, request(), NOW);

    expect(outcome.replayed).toBe(false);
    expect(outcome.transaction.status).toBe('completed');
    expect(outcome.transaction.amountPiastres).toBe(76_000);
    expect(walletBalance(data)).toBe(walletBalance(seed) - 76_000);
    expect(data.ledger).toHaveLength(seed.ledger.length + 1);
    expect(data.ledger.at(-1)?.transactionId).toBe(outcome.transaction.id);
    expect(ledgerIsConsistent(data, DEMO_IDS.company)).toBe(true);
    expect(data.tags[`tag-${DEMO_IDS.vehicleCorolla}`]?.cooldownUntil).toBe('2026-10-12T15:00:00.000Z');
  });

  it('is idempotent: replaying the same clientTxnId debits only once', () => {
    const seed = createSeedDataset(NOW);
    const first = recordFuelTransaction(seed, request(), NOW);
    const second = recordFuelTransaction(first.data, request(), NOW);

    expect(second.outcome.replayed).toBe(true);
    expect(second.outcome.transaction.id).toBe(first.outcome.transaction.id);
    expect(second.data).toBe(first.data);
    expect(walletBalance(second.data)).toBe(walletBalance(first.data));
  });

  it('declines a second fueling within the cooldown window without touching the wallet', () => {
    const seed = createSeedDataset(NOW);
    const first = recordFuelTransaction(seed, request(), NOW);
    const later = new Date(NOW.getTime() + 60 * 60_000);
    const second = recordFuelTransaction(
      first.data,
      request({ clientTxnId: 'client-2', volumeMl: 5_000, deviceAt: later.toISOString() }),
      later,
    );

    expect(second.outcome.transaction.status).toBe('declined');
    expect(second.outcome.transaction.declineReason).toBe('cooldown');
    expect(walletBalance(second.data)).toBe(walletBalance(first.data));
    expect(second.data.ledger).toHaveLength(first.data.ledger.length);
  });

  it('declines when the daily volume limit would be exceeded', () => {
    const seed = createSeedDataset(NOW);
    const { outcome } = recordFuelTransaction(seed, request({ volumeMl: 55_000 }), NOW);
    expect(outcome.transaction.declineReason).toBe('limitExceeded');
  });

  it('declines online when the wallet balance would go below zero', () => {
    const seed = createSeedDataset(NOW);
    const poor: Dataset = {
      ...seed,
      wallets: { ...seed.wallets, [DEMO_IDS.company]: { id: 'wal-co-nile', companyId: DEMO_IDS.company, balancePiastres: 10_000 } },
    };
    const { data, outcome } = recordFuelTransaction(poor, request(), NOW);
    expect(outcome.transaction.declineReason).toBe('insufficientBalance');
    expect(walletBalance(data)).toBe(10_000);
  });

  it('commits an offline transaction at sync even when it conflicts, and raises an alert', () => {
    const seed = createSeedDataset(NOW);
    const poor: Dataset = {
      ...seed,
      wallets: { ...seed.wallets, [DEMO_IDS.company]: { id: 'wal-co-nile', companyId: DEMO_IDS.company, balancePiastres: 10_000 } },
    };
    const capturedAt = new Date(NOW.getTime() - 30 * 60_000);
    const { data, outcome } = recordFuelTransaction(
      poor,
      request({ origin: 'offline', deviceAt: capturedAt.toISOString() }),
      NOW,
    );
    expect(outcome.transaction.status).toBe('completed');
    expect(outcome.transaction.syncedAt).toBe(NOW.toISOString());
    expect(walletBalance(data)).toBe(10_000 - 76_000);
    expect(data.alerts.at(-1)?.type).toBe('syncConflict');
  });

  it('records VIP fueling as non-billable with no ledger entry', () => {
    const seed = createSeedDataset(NOW);
    const { data, outcome } = recordFuelTransaction(
      seed,
      request({ tagId: `tag-${DEMO_IDS.vehicleVip}`, clientTxnId: 'vip-1' }),
      NOW,
    );
    expect(outcome.transaction.status).toBe('completed');
    expect(outcome.transaction.billable).toBe(false);
    expect(data.ledger).toHaveLength(seed.ledger.length);
    expect(walletBalance(data)).toBe(walletBalance(seed));
  });
});
