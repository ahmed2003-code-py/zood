import { describe, expect, it } from 'vitest';
import { createSeedDataset, DEMO_IDS } from '@/mocks/seed';
import { receiptFromTransaction } from '@/features/receipt/model';
import { recordFuelTransaction, type FuelRequest } from './fueling';
import { evaluateScan, provisionalSnapshot, validateOffline, walletStatus, type QueuedFuel } from './pos';

const NOW = new Date('2026-10-12T12:00:00Z'); // 15:00 Cairo
const tagOf = (vehicleId: string) => `tag-${vehicleId}`;

function request(overrides: Partial<FuelRequest> = {}): FuelRequest {
  return {
    clientTxnId: 'c-1',
    tagId: tagOf(DEMO_IDS.vehicleCorolla),
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

describe('evaluateScan', () => {
  const seed = createSeedDataset(NOW);

  it('accepts the hero vehicle with wallet status as a word and 50 L remaining', () => {
    const result = evaluateScan(seed, { tagId: tagOf(DEMO_IDS.vehicleCorolla), stationId: DEMO_IDS.station, now: NOW });
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.walletStatus).toBe('available');
    expect(result.remainingMl).toBe(50_000);
    expect(result.unitPricePiastres).toBe(1900);
  });

  it('reads the cooldown from the tag record first', () => {
    const until = new Date(NOW.getTime() + 3 * 3600_000).toISOString();
    const result = evaluateScan(seed, { tagId: tagOf(DEMO_IDS.vehicleCorolla), stationId: DEMO_IDS.station, now: NOW, tagCooldownUntil: until });
    expect(result).toMatchObject({ ok: false, reason: 'cooldown', cooldownUntil: until });
  });

  it('shows the truck with 20 L left so a 40 L request is declined at confirmation', () => {
    const result = evaluateScan(seed, { tagId: tagOf(DEMO_IDS.vehicleFuso), stationId: DEMO_IDS.station, now: NOW });
    expect(result.ok).toBe(true);
    if (result.ok) expect(result.remainingMl).toBe(20_000);
    const outcome = recordFuelTransaction(seed, request({ tagId: tagOf(DEMO_IDS.vehicleFuso) }), NOW).outcome;
    expect(outcome.transaction.declineReason).toBe('limitExceeded');
  });

  it('reports low balance and suspended accounts', () => {
    expect(walletStatus(seed, 'co-delta')).toBe('low');
    expect(walletStatus(seed, 'co-sinai')).toBe('suspended');
    expect(evaluateScan(seed, { tagId: tagOf(DEMO_IDS.vehicleSinai), stationId: DEMO_IDS.station, now: NOW })).toMatchObject({
      ok: false,
      reason: 'companyFrozen',
    });
  });

  it('declines a suspended tag', () => {
    const tagId = tagOf(DEMO_IDS.vehicleAnomaly);
    const tag = seed.tags[tagId]!;
    const data = { ...seed, tags: { ...seed.tags, [tagId]: { ...tag, status: 'deactivated' as const } } };
    expect(evaluateScan(data, { tagId, stationId: DEMO_IDS.station, now: NOW })).toMatchObject({ ok: false, reason: 'tagInactive' });
  });
});

describe('offline validation', () => {
  it('applies pending captures provisionally so the device cannot overspend the daily limit', () => {
    const seed = createSeedDataset(NOW);
    const queued: QueuedFuel = { request: request({ origin: 'offline', clientTxnId: 'off-1' }), amountPiastres: 76_000, unitPricePiastres: 1900, capturedAt: NOW.toISOString() };
    const snapshot = provisionalSnapshot(seed, [queued]);
    expect(snapshot.wallets[DEMO_IDS.company]?.balancePiastres).toBe(84_235_000 - 76_000);
    // 40 L already captured offline today: another 20 L exceeds the 50 L daily limit.
    expect(validateOffline(snapshot, request({ origin: 'offline', clientTxnId: 'off-2', volumeMl: 20_000 }), 38_000, NOW)).toBe('limitExceeded');
    expect(validateOffline(snapshot, request({ origin: 'offline', clientTxnId: 'off-3', volumeMl: 10_000 }), 19_000, NOW)).toBeUndefined();
  });
});

describe('receipt', () => {
  it('keeps the original receipt language for reprints', () => {
    const seed = createSeedDataset(NOW);
    const { data, outcome } = recordFuelTransaction(seed, request({ receiptLocale: 'en' }), NOW);
    const receipt = receiptFromTransaction(data, outcome.transaction);
    expect(receipt.locale).toBe('en');
    expect(receipt.amountPiastres).toBe(76_000);
    expect(receipt.plate).toBe('أ ب ج 4582');
  });
});
