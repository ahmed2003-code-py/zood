import { createHmac } from 'node:crypto';
import type { BrowserContext, Route } from '@playwright/test';
import type { components } from '../../src/api/generated/schema';

/**
 * The device channel of the live POS (B4 contract §1), answered without a server: activation, the
 * device token, the agent session, the snapshot and fuelings. The snapshot names tags the way the
 * server does (the first 32 bits of HMAC-SHA256(tagKey, UID)), so the terminal's own lookup runs for
 * real. Routes registered here win over the generic API stub.
 */
type S = components['schemas'];

const TAG_KEY = Buffer.from('zawed-e2e-tag-key-0123456789abcd').toString('base64');
const digest = (uid: string) => createHmac('sha256', Buffer.from(TAG_KEY, 'base64')).update(uid).digest().subarray(0, 4).toString('hex');

export const POS_TAGS = {
  /** A vehicle that can fuel (GAS_92, 40 L left today). */
  ok: '04A1B2C3D4',
  /** A vehicle in cooldown. */
  cooldown: '04C0FFEE00',
  /** A vehicle whose company wallet is suspended. */
  suspended: '04BADBAD01',
} as const;

export interface PosStubOptions {
  now: string;
  mustChangePin?: boolean;
  /** Answer the first sign-in with AGENT_LOCKED for this many seconds. */
  lockedFor?: number;
  /** Answer the first sign-in with a wrong PIN and this many attempts left. */
  attemptsLeft?: number;
}

export function posSnapshot(now: string): S['PosSnapshot'] {
  const later = new Date(Date.parse(now) + 47 * 60_000).toISOString();
  return {
    contract: 1,
    serverTime: now,
    vipEnabled: true,
    tagKey: TAG_KEY,
    station: { id: 's0000001-0000-7000-8000-000000000001', nameAr: 'محطة التجمع الخامس', nameEn: 'Fifth Settlement Station', timeZone: 'Africa/Cairo', fuelTypes: ['GAS_92', 'GAS_95', 'DIESEL'], open: true },
    prices: [
      { fuelType: 'GAS_92', national: true, pricePiastresPerLitre: 1725, effectiveFrom: '2026-10-01T00:00:00Z' },
      { fuelType: 'GAS_95', national: true, pricePiastresPerLitre: 1900, effectiveFrom: '2026-10-01T00:00:00Z' },
      { fuelType: 'DIESEL', national: true, pricePiastresPerLitre: 1550, effectiveFrom: '2026-10-01T00:00:00Z' },
    ],
    tags: [
      { digest: digest(POS_TAGS.ok), vehicleId: 'v1', status: 'ACTIVE' },
      { digest: digest(POS_TAGS.cooldown), vehicleId: 'v2', status: 'ACTIVE', cooldownUntil: later },
      { digest: digest(POS_TAGS.suspended), vehicleId: 'v3', status: 'ACTIVE' },
    ],
    vehicles: [
      { id: 'v1', companyId: 'c1', plate: 'ق ط ر 4821', fuelType: 'GAS_92', vip: false, tankCapacityMl: 80_000, active: true },
      { id: 'v2', companyId: 'c1', plate: 'م ص ن 1902', fuelType: 'DIESEL', vip: false, tankCapacityMl: 100_000, active: true },
      { id: 'v3', companyId: 'c2', plate: 'س ع د 2210', fuelType: 'GAS_92', vip: false, tankCapacityMl: 50_000, active: true },
    ],
    limits: [
      { vehicleId: 'v1', kind: 'VOLUME', period: 'DAY', remaining: 40_000, periodStart: now },
      { vehicleId: 'v1', kind: 'AMOUNT', period: 'MONTH', remaining: 450_000, periodStart: now },
    ],
    companies: [
      { id: 'c1', nameAr: 'النيل للنقل', nameEn: 'Nile Transport', active: true, cooldownMinutes: 120, walletStatus: 'AVAILABLE', offlineAllowancePiastres: 500_000 },
      { id: 'c2', nameAr: 'القاهرة للتوزيع', nameEn: 'Cairo Distribution', active: true, cooldownMinutes: 120, walletStatus: 'SUSPENDED', offlineAllowancePiastres: 0 },
    ],
  } as S['PosSnapshot'];
}

/** Installs the device-channel routes. Call after installApiStub (later routes take precedence). */
export async function installPosStub(context: BrowserContext, options: PosStubOptions) {
  let signIns = 0;
  let mustChangePin = options.mustChangePin ?? false;
  let sequence = 10_480;
  const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, json: body });

  await context.route('**/api/v1/pos/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname.replace(/^\/api\/v1/, '');
    if (path === '/pos/activate') return json(route, { deviceId: 'p0000001-0000-7000-8000-000000000001', deviceCode: 'POS-201', stationId: 's0000001-0000-7000-8000-000000000001' }, 201);
    if (path === '/pos/token') return json(route, { token: 'device-token', expiresAt: new Date(Date.parse(options.now) + 3_600_000).toISOString() });
    if (path === '/pos/snapshot') return json(route, posSnapshot(options.now));
    if (path === '/pos/sync') return json(route, { batchId: 'b', results: [] });
    if (path === '/pos/agents/logout') return route.fulfill({ status: 204 });
    if (path === '/pos/agents/pin') {
      mustChangePin = false;
      return route.fulfill({ status: 204 });
    }
    if (path === '/pos/agents/login') {
      signIns += 1;
      if (signIns === 1 && options.lockedFor) {
        return json(route, { code: 'AGENT_LOCKED', message: 'locked', retryable: false, params: { retryAfterSeconds: options.lockedFor } }, 423);
      }
      if (signIns === 1 && options.attemptsLeft !== undefined) {
        return json(route, { code: 'AGENT_SIGN_IN_FAILED', message: 'wrong', retryable: false, params: { attemptsLeft: options.attemptsLeft } }, 401);
      }
      return json(route, {
        sessionToken: 'agent-session',
        expiresAt: new Date(Date.parse(options.now) + 10 * 3_600_000).toISOString(),
        agent: { id: 'g0000001-0000-7000-8000-000000000001', workerId: 'W-100', fullName: 'محمود سعيد', mustChangePin },
      });
    }
    if (path === '/pos/fuelings' && request.method() === 'POST') {
      const body = request.postDataJSON() as S['PosFuelingRequest'];
      sequence += 1;
      const amount = Math.round((body.volumeMl * 1725) / 1000);
      const fueling: S['PosFuelingResponse'] = {
        id: `f${sequence}-0000-7000-8000-000000000001`,
        reference: `F-${sequence}`,
        status: 'COMPLETED',
        plate: 'ق ط ر 4821',
        companyNameAr: 'النيل للنقل',
        companyNameEn: 'Nile Transport',
        stationNameAr: 'محطة التجمع الخامس',
        stationNameEn: 'Fifth Settlement Station',
        agentWorkerId: 'W-100',
        fuelType: 'GAS_92',
        volumeMl: body.volumeMl,
        unitPricePiastres: 1725,
        amountPiastres: amount,
        billable: true,
        walletStatus: 'AVAILABLE',
        evaluatedAt: options.now,
        receiptLocale: body.receiptLocale ?? 'ar',
        receiptPath: `/api/v1/receipts/f${sequence}?sig=e2e`,
        replayed: false,
      };
      return json(route, fueling, 201);
    }
    return json(route, { code: 'NOT_FOUND', message: `pos stub: ${path}` }, 404);
  });
}
