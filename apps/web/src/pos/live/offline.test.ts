import { describe, expect, it } from 'vitest';
import { decideOffline, normaliseUid, periodKey, priceAt, tagDigest, type QueuedCapture, type Snapshot } from './offline';

const NOW = new Date('2026-11-15T08:00:00Z'); // Sunday 10:00 in Cairo

function snapshot(overrides: Partial<Snapshot> = {}): Snapshot {
  return {
    serverTime: '2026-11-15T07:00:00Z',
    contract: 1,
    vipEnabled: false,
    station: { id: 's1', nameAr: 'م', nameEn: 'S', timeZone: 'Africa/Cairo', fuelTypes: ['GAS_92'], open: true },
    prices: [
      { fuelType: 'GAS_92', national: true, pricePiastresPerLitre: 1500, effectiveFrom: '2026-11-01T00:00:00Z' },
      { fuelType: 'GAS_92', national: false, pricePiastresPerLitre: 1575, effectiveFrom: '2026-11-10T00:00:00Z' },
    ],
    tagKey: 'a2V5',
    tags: [{ digest: 'd1', tagId: 't1', vehicleId: 'v1', status: 'ACTIVE' }],
    vehicles: [{ id: 'v1', companyId: 'c1', plate: 'ق ر 1', fuelType: 'GAS_92', vip: false, tankCapacityMl: 60000, active: true }],
    limits: [{ vehicleId: 'v1', kind: 'VOLUME', period: 'DAY', maxValue: 50000, used: 30000, periodStart: '2026-11-14T22:00:00Z' }],
    companies: [{ id: 'c1', nameAr: 'ش', nameEn: 'C', active: true, cooldownMinutes: 180, walletStatus: 'AVAILABLE', offlineAllowancePiastres: 40000 }],
    ...overrides,
  } as Snapshot;
}

function queued(overrides: Partial<QueuedCapture> = {}): QueuedCapture {
  return {
    clientTxnId: crypto.randomUUID(),
    tagUid: '04A1B2C3',
    digest: 'd1',
    vehicleId: 'v1',
    companyId: 'c1',
    plate: 'ق ر 1',
    fuelType: 'GAS_92',
    volumeMl: 5000,
    unitPricePiastres: 1575,
    amountPiastres: 7875,
    billable: true,
    deviceAt: '2026-11-15T03:00:00Z',
    agentId: 'a1',
    receiptLocale: 'ar',
    ...overrides,
  };
}

describe('decideOffline', () => {
  it('approves within the limit and the allowance, at the price in force', () => {
    const decision = decideOffline(snapshot(), [], { digest: 'd1', volumeMl: 10000, now: NOW });
    expect(decision).toMatchObject({ ok: true, unitPricePiastres: 1575, amountPiastres: 15750, billable: true });
  });

  it('refuses an unknown tag', () => {
    expect(decideOffline(snapshot(), [], { digest: 'nope', volumeMl: 1000, now: NOW })).toMatchObject({ reason: 'UNKNOWN_TAG' });
  });

  it('applies its own queued capture: cooldown from the last capture of the tag', () => {
    const recent = queued({ deviceAt: '2026-11-15T07:30:00Z' });
    expect(decideOffline(snapshot(), [recent], { digest: 'd1', volumeMl: 1000, now: NOW })).toMatchObject({ reason: 'COOLDOWN' });
  });

  it('counts queued captures against the limit and the allowance', () => {
    const earlier = queued({ volumeMl: 15000, amountPiastres: 23625 });
    expect(decideOffline(snapshot(), [earlier], { digest: 'd1', volumeMl: 6000, now: NOW })).toMatchObject({ reason: 'LIMIT_EXCEEDED' });
    const roomy = snapshot({ limits: [] });
    expect(decideOffline(roomy, [earlier], { digest: 'd1', volumeMl: 12000, now: NOW })).toMatchObject({
      reason: 'INSUFFICIENT_BALANCE',
    });
  });

  it('forgets the snapshot use once a new period starts', () => {
    const tomorrow = new Date('2026-11-16T08:00:00Z');
    expect(decideOffline(snapshot(), [], { digest: 'd1', volumeMl: 25000, now: tomorrow })).toMatchObject({ ok: true });
  });

  it('checks in the server order: tag before fuel before limits', () => {
    const lost = snapshot({ tags: [{ digest: 'd1', tagId: 't1', vehicleId: 'v1', status: 'REPLACEMENT_PENDING' }] });
    expect(decideOffline(lost, [], { digest: 'd1', volumeMl: 99999, fuelType: 'DIESEL', now: NOW })).toMatchObject({
      reason: 'TAG_INACTIVE',
    });
    expect(decideOffline(snapshot(), [], { digest: 'd1', volumeMl: 99999, fuelType: 'DIESEL', now: NOW })).toMatchObject({
      reason: 'FUEL_TYPE_MISMATCH',
    });
  });

  it('treats a VIP vehicle as normal while the VIP flow is off', () => {
    const vip = snapshot({ vehicles: [{ id: 'v1', companyId: 'c1', plate: 'p', fuelType: 'GAS_92', vip: true, tankCapacityMl: 1, active: true }] });
    expect(decideOffline(vip, [], { digest: 'd1', volumeMl: 30000, now: NOW })).toMatchObject({ reason: 'LIMIT_EXCEEDED' });
    expect(decideOffline({ ...vip, vipEnabled: true }, [], { digest: 'd1', volumeMl: 30000, now: NOW })).toMatchObject({
      ok: true,
      billable: false,
    });
  });
});

describe('helpers', () => {
  it('picks the price that took effect last, the station on a tie', () => {
    const prices = snapshot().prices;
    expect(priceAt(prices, 'GAS_92', new Date('2026-11-05T00:00:00Z'))).toBe(1500);
    expect(priceAt(prices, 'GAS_92', NOW)).toBe(1575);
    expect(priceAt(prices, 'DIESEL', NOW)).toBeUndefined();
  });

  it('names periods in the station zone with weeks starting on Saturday', () => {
    expect(periodKey(new Date('2026-11-14T22:30:00Z'), 'DAY', 'Africa/Cairo')).toBe('2026-11-15');
    expect(periodKey(NOW, 'WEEK', 'Africa/Cairo')).toBe('2026-11-14');
    expect(periodKey(NOW, 'MONTH', 'Africa/Cairo')).toBe('2026-11');
  });

  it('normalises a UID the way the server does and digests it', async () => {
    expect(normaliseUid('04:a1:b2:c3')).toBe('04A1B2C3');
    expect(normaliseUid('xyz')).toBeUndefined();
    const digest = await tagDigest(btoa('key'), '04A1B2C3');
    expect(digest).toMatch(/^[0-9a-f]{64}$/);
    expect(await tagDigest(btoa('key'), '04A1B2C3')).toBe(digest);
  });
});
