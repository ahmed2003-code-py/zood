import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { FuelRequest } from '@/domain/fueling';
import { DEMO_IDS } from './seed';
import { useDemoStore } from './store';

function request(overrides: Partial<FuelRequest> = {}): FuelRequest {
  return {
    clientTxnId: crypto.randomUUID(),
    tagId: `tag-${DEMO_IDS.vehicleCorolla}`,
    stationId: DEMO_IDS.station,
    deviceId: DEMO_IDS.device,
    agentId: DEMO_IDS.agent,
    volumeMl: 40_000,
    origin: 'online',
    deviceAt: new Date().toISOString(),
    receiptLocale: 'ar',
    ...overrides,
  };
}

const balance = () => useDemoStore.getState().data.wallets[DEMO_IDS.company]?.balancePiastres;

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  useDemoStore.getState().reset();
});

afterEach(() => vi.useRealTimers());

describe('demo store', () => {
  it('resets to the exact initial demo state', () => {
    expect(balance()).toBe(84_235_000);
    const state = useDemoStore.getState();
    expect(state.simulation).toEqual({ network: 'online', printer: 'ready', selectedTagId: `tag-${DEMO_IDS.vehicleCorolla}` });
    expect(state.posQueue).toEqual([]);
    expect(state.tagMemory).toEqual({});
    useDemoStore.getState().recordFuel(request());
    useDemoStore.getState().reset();
    expect(balance()).toBe(84_235_000);
    expect(useDemoStore.getState().tagMemory).toEqual({});
  });

  it('online fueling debits once even when the POS retries, and writes the tag cooldown', () => {
    const same = request();
    useDemoStore.getState().recordFuel(same);
    useDemoStore.getState().recordFuel(same);
    expect(balance()).toBe(84_235_000 - 76_000);
    expect(useDemoStore.getState().tagMemory[same.tagId]).toBeTruthy();
  });

  it('offline capture is queued (not posted) and syncs exactly once when the network returns', () => {
    const store = useDemoStore.getState();
    store.setNetwork('offline');
    const capture = request({ origin: 'offline' });
    expect(store.captureOffline(capture).ok).toBe(true);
    expect(useDemoStore.getState().captureOffline(capture).ok).toBe(true); // idempotent on the device
    expect(useDemoStore.getState().posQueue).toHaveLength(1);
    expect(balance()).toBe(84_235_000);

    useDemoStore.getState().setNetwork('online');
    expect(useDemoStore.getState().sync.status).toBe('syncing');
    vi.runAllTimers();

    const state = useDemoStore.getState();
    expect(state.posQueue).toHaveLength(0);
    expect(state.sync.lastSyncedCount).toBe(1);
    expect(balance()).toBe(84_235_000 - 76_000);
    const synced = state.data.transactions.filter((txn) => txn.clientTxnId === capture.clientTxnId);
    expect(synced).toHaveLength(1);
    expect(synced[0]?.origin).toBe('offline');
    expect(synced[0]?.syncedAt).toBeTruthy();
  });

  it('declines an offline capture beyond the cached daily limit', () => {
    useDemoStore.getState().setNetwork('offline');
    expect(useDemoStore.getState().captureOffline(request({ origin: 'offline' })).ok).toBe(true);
    const second = useDemoStore.getState().captureOffline(request({ origin: 'offline', volumeMl: 20_000 }));
    expect(second).toEqual({ ok: false, reason: 'limitExceeded' });
  });

  it('the tank anomaly trigger completes the sale and raises a non-blocking critical alert', () => {
    const outcome = useDemoStore.getState().triggerTankAnomaly();
    expect(outcome?.transaction.status).toBe('completed');
    const alert = useDemoStore.getState().data.alerts.find((item) => item.transactionId === outcome?.transaction.id);
    expect(alert).toMatchObject({ type: 'tankCapacityExceeded', severity: 'critical', status: 'open' });
  });
});
