import { describe, expect, it } from 'vitest';
import { ApiRequestError } from '@/api/http-client';
import { checkTag, formatCountdown, pumpFuelBlock, quote, type KnownTag } from './flow';
import type { QueuedCapture, Snapshot } from './offline';
import { readSignInRefusal } from './sign-in-state';

const NOW = new Date('2026-11-15T08:00:00Z'); // Sunday 10:00 in Cairo

function snapshot(overrides: Partial<Snapshot> = {}): Snapshot {
  return {
    serverTime: '2026-11-15T07:00:00Z',
    contract: 1,
    vipEnabled: true,
    station: { id: 's1', nameAr: 'م', nameEn: 'S', timeZone: 'Africa/Cairo', fuelTypes: ['GAS_92', 'DIESEL'], open: true },
    prices: [{ fuelType: 'GAS_92', national: true, pricePiastresPerLitre: 1725, effectiveFrom: '2026-11-01T00:00:00Z' }],
    tagKey: 'a2V5',
    tags: [{ digest: 'd1', vehicleId: 'v1', status: 'ACTIVE' }],
    vehicles: [{ id: 'v1', companyId: 'c1', plate: 'ق ر 1', fuelType: 'GAS_92', vip: false, tankCapacityMl: 60000, active: true }],
    limits: [
      { vehicleId: 'v1', kind: 'VOLUME', period: 'MONTH', remaining: 300000, periodStart: '2026-10-31T22:00:00Z' },
      { vehicleId: 'v1', kind: 'VOLUME', period: 'DAY', remaining: 40000, periodStart: '2026-11-14T22:00:00Z' },
    ],
    companies: [{ id: 'c1', nameAr: 'النيل', nameEn: 'Nile', active: true, cooldownMinutes: 120, walletStatus: 'LOW', offlineAllowancePiastres: 40000 }],
    ...overrides,
  } as Snapshot;
}

function queued(overrides: Partial<QueuedCapture> = {}): QueuedCapture {
  return {
    clientTxnId: crypto.randomUUID(),
    deviceId: 'dev1',
    tagUid: '04A1B2C3',
    digest: 'd1',
    vehicleId: 'v1',
    companyId: 'c1',
    plate: 'ق ر 1',
    fuelType: 'GAS_92',
    volumeMl: 15000,
    unitPricePiastres: 1725,
    amountPiastres: 25875,
    billable: true,
    deviceAt: '2026-11-15T04:00:00Z',
    agentId: 'a1',
    receiptLocale: 'ar',
    ...overrides,
  };
}

const known = (check: ReturnType<typeof checkTag>) => {
  if (!check.known) throw new Error(`unknown: ${check.reason}`);
  return check;
};

describe('checkTag (verify step)', () => {
  it('shows what the agent needs: plate, company, fuel, wallet word, limits left, price', () => {
    const check = known(checkTag(snapshot(), [], 'd1', NOW));
    expect(check).toMatchObject({
      plate: 'ق ر 1',
      companyNameEn: 'Nile',
      fuelType: 'GAS_92',
      walletStatus: 'LOW',
      billable: true,
      unitPricePiastres: 1725,
      block: undefined,
      cooldownUntil: undefined,
    });
    // The day's limit comes first.
    expect(check.limits.map((limit) => limit.period)).toEqual(['DAY', 'MONTH']);
    expect(check.limits[0]).toEqual({ kind: 'VOLUME', period: 'DAY', remaining: 40000 });
  });

  it('takes this terminal’s own captures off what is left, in integer millilitres', () => {
    const check = known(checkTag(snapshot(), [queued({ volumeMl: 15500 })], 'd1', NOW));
    expect(check.limits[0]!.remaining).toBe(24500);
    expect(check.limits[1]!.remaining).toBe(284500);
  });

  it('declines when the day is used up', () => {
    const check = known(checkTag(snapshot(), [queued({ volumeMl: 40000, deviceAt: '2026-11-15T01:00:00Z' })], 'd1', NOW));
    expect(check.limits[0]!.remaining).toBe(0);
    expect(check.block).toBe('LIMIT_EXCEEDED');
  });

  it('declines during a cooldown and says until when', () => {
    const check = known(checkTag(snapshot(), [queued({ deviceAt: '2026-11-15T07:30:00Z' })], 'd1', NOW));
    expect(check.block).toBe('COOLDOWN');
    expect(check.cooldownUntil).toBe('2026-11-15T09:30:00.000Z');
  });

  it('declines a suspended wallet or a frozen company', () => {
    const suspended = snapshot();
    suspended.companies[0]!.walletStatus = 'SUSPENDED';
    expect(known(checkTag(suspended, [], 'd1', NOW)).block).toBe('COMPANY_FROZEN');
    const frozen = snapshot();
    frozen.companies[0]!.active = false;
    expect(known(checkTag(frozen, [], 'd1', NOW)).block).toBe('COMPANY_FROZEN');
  });

  it('declines a blocked tag before a blocked vehicle', () => {
    const data = snapshot({ tags: [{ digest: 'd1', vehicleId: 'v1', status: 'LOST' }] });
    data.vehicles[0]!.active = false;
    expect(known(checkTag(data, [], 'd1', NOW)).block).toBe('TAG_INACTIVE');
  });

  it('declines a fuel the station does not sell', () => {
    const data = snapshot();
    data.vehicles[0]!.fuelType = 'CNG';
    const check = known(checkTag(data, [], 'd1', NOW));
    expect(check.block).toBe('FUEL_NOT_AVAILABLE');
    expect(check.unitPricePiastres).toBeUndefined();
  });

  it('does not apply limits to a VIP vehicle and marks it not billed', () => {
    const data = snapshot();
    data.vehicles[0]!.vip = true;
    const check = known(checkTag(data, [], 'd1', NOW));
    expect(check.billable).toBe(false);
    expect(check.limits).toEqual([]);
  });

  it('cannot check without a snapshot, with a stale one, or for an unknown tag', () => {
    expect(checkTag(undefined, [], 'd1', NOW)).toEqual({ known: false, reason: 'NO_SNAPSHOT' });
    expect(checkTag(snapshot({ serverTime: '2026-11-13T07:00:00Z' }), [], 'd1', NOW)).toEqual({ known: false, reason: 'SNAPSHOT_STALE' });
    expect(checkTag(snapshot(), [], 'other', NOW)).toEqual({ known: false, reason: 'UNKNOWN_TAG' });
  });

  it('skips a limit whose period began after the snapshot', () => {
    const lateNight = new Date('2026-11-15T22:30:00Z'); // the next Cairo day
    const check = known(checkTag(snapshot({ serverTime: '2026-11-15T21:00:00Z' }), [], 'd1', lateNight));
    expect(check.limits.map((limit) => limit.period)).toEqual(['MONTH']);
  });
});

describe('pumpFuelBlock (wrong fuel)', () => {
  it('refuses a pump fuel that is not the vehicle’s', () => {
    const check = known(checkTag(snapshot(), [], 'd1', NOW));
    expect(pumpFuelBlock(check, 'GAS_92')).toBeUndefined();
    expect(pumpFuelBlock(check, 'DIESEL')).toBe('FUEL_TYPE_MISMATCH');
  });
});

describe('quote (confirm step)', () => {
  const check = known(checkTag(snapshot(), [], 'd1', NOW));

  it('prices the volume in integer piastres, rounding half up', () => {
    // 25.333 L × 17.25 EGP = 437.00 EGP (436.99425 rounds to 43699).
    expect(quote(check, 25333)).toMatchObject({ volumeMl: 25333, amountPiastres: 43699, unitPricePiastres: 1725, billable: true, overTank: false });
  });

  it('warns when the volume is over what a limit has left, or over the tank', () => {
    expect(quote(check, 40001).overLimit).toEqual({ kind: 'VOLUME', period: 'DAY', remaining: 40000 });
    expect(quote(check, 40000).overLimit).toBeUndefined();
    expect(quote(check, 60001).overTank).toBe(true);
  });

  it('checks an amount limit against the amount', () => {
    const amount: KnownTag = { ...check, limits: [{ kind: 'AMOUNT', period: 'DAY', remaining: 17250 }] };
    expect(quote(amount, 10000).overLimit).toBeUndefined();
    expect(quote(amount, 10001).overLimit?.kind).toBe('AMOUNT');
  });

  it('leaves the amount to the server when the terminal could not check the tag', () => {
    expect(quote({ known: false, reason: 'UNKNOWN_TAG' }, 20000)).toEqual({ volumeMl: 20000, billable: true, overTank: false });
  });
});

describe('formatCountdown', () => {
  it('counts down in minutes and seconds, then hours', () => {
    expect(formatCountdown(90_000, 0)).toBe('1:30');
    expect(formatCountdown(899_500, 0)).toBe('15:00');
    expect(formatCountdown(3_725_000, 0)).toBe('1:02:05');
    expect(formatCountdown(0, 5_000)).toBe('0:00');
  });
});

describe('readSignInRefusal (lockout)', () => {
  const refusal = (code: string, params?: Record<string, unknown>) =>
    new ApiRequestError({ status: code === 'AGENT_LOCKED' ? 423 : 401, code, message: '', retryable: false, params });

  it('turns retryAfterSeconds into the instant the lockout ends', () => {
    expect(readSignInRefusal(refusal('AGENT_LOCKED', { retryAfterSeconds: 840 }), 1_000)).toEqual({ code: 'AGENT_LOCKED', lockedUntilMs: 841_000 });
  });

  it('assumes the standard 15 minutes when the API gives no duration', () => {
    expect(readSignInRefusal(refusal('AGENT_LOCKED'), 0).lockedUntilMs).toBe(900_000);
  });

  it('reads the attempts left when the API sends them', () => {
    expect(readSignInRefusal(refusal('AGENT_SIGN_IN_FAILED', { attemptsLeft: 2 }), 0)).toEqual({ code: 'AGENT_SIGN_IN_FAILED', attemptsLeft: 2 });
    expect(readSignInRefusal(refusal('AGENT_SIGN_IN_FAILED'), 0)).toEqual({ code: 'AGENT_SIGN_IN_FAILED', attemptsLeft: undefined });
    expect(readSignInRefusal(new Error('boom'), 0)).toEqual({ code: 'UNKNOWN' });
  });
});
