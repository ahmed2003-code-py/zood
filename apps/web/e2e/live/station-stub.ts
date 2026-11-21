import type { BrowserContext, Route } from '@playwright/test';
import type { components } from '../../src/api/generated/schema';

/**
 * Station-portal fixtures on top of the generic API stub (api-stub.ts): devices and agents with
 * health problems, shifts that match the stub's fuelings, payouts, a statement with a held fueling,
 * a verified bank account and an open ticket. Routes registered here win over the generic stub.
 */
type S = components['schemas'];

const NOW = Date.parse('2026-11-18T09:30:00Z');
const HOUR = 3_600_000;
const at = (hoursAgo: number) => new Date(NOW - hoursAgo * HOUR).toISOString();
const id = (prefix: string, n: number) => `${prefix}-0000-7000-8000-${String(n).padStart(12, '0')}`;
const OPERATOR_ID = id('o0000001', 1);
const station = (n: number) => id('s0000001', n);
const device = (n: number) => id('p0000001', n);

const devices: S['DeviceResponse'][] = [
  { id: device(1), code: 'POS-201', label: 'Pump island 1', stationId: station(1), status: 'ACTIVE', activationExpired: false, lastSyncAt: at(0.1), boundAt: at(900), operatorId: OPERATOR_ID, createdAt: at(1400) },
  { id: device(2), code: 'POS-202', label: 'Pump island 2', stationId: station(2), status: 'ACTIVE', activationExpired: false, lastSyncAt: at(5), boundAt: at(900), operatorId: OPERATOR_ID, createdAt: at(1400) },
  { id: device(3), code: 'POS-203', label: 'Diesel lane', stationId: station(3), status: 'PENDING_ACTIVATION', activationExpired: true, activationExpiresAt: at(20), operatorId: OPERATOR_ID, createdAt: at(30) },
  { id: device(4), code: 'POS-204', label: 'Spare terminal', stationId: station(1), status: 'PENDING_ACTIVATION', activationExpired: false, activationExpiresAt: new Date(NOW + 10 * 60_000).toISOString(), operatorId: OPERATOR_ID, createdAt: at(1) },
];

const agents: S['AgentResponse'][] = [
  { id: id('g0000001', 1), fullName: 'محمود سعيد', workerId: 'W-100', stationId: station(1), status: 'ACTIVE', mustChangePin: false, locked: false, operatorId: OPERATOR_ID, createdAt: at(1200) },
  { id: id('g0000001', 2), fullName: 'عمر جمال', workerId: 'W-101', stationId: station(2), status: 'ACTIVE', mustChangePin: false, locked: true, lockedUntil: new Date(NOW + 9 * 60_000).toISOString(), operatorId: OPERATOR_ID, createdAt: at(1200) },
  { id: id('g0000001', 3), fullName: 'حسن علي', workerId: 'W-102', stationId: station(3), status: 'ACTIVE', mustChangePin: true, locked: false, operatorId: OPERATOR_ID, createdAt: at(30) },
  { id: id('g0000001', 4), fullName: 'كريم فتحي', workerId: 'W-103', stationId: station(1), status: 'SUSPENDED', mustChangePin: false, locked: false, operatorId: OPERATOR_ID, createdAt: at(2000) },
];

// The generic stub's fuelings run on devices 1–3 with workers W-100…W-103 over the last days.
const shifts: S['Shift'][] = [
  { id: id('h0000001', 1), agentId: agents[0]!.id, agentName: agents[0]!.fullName, workerId: 'W-100', deviceId: device(1), deviceCode: 'POS-201', stationId: station(1), startedAt: at(4), endedAt: undefined },
  { id: id('h0000001', 2), agentId: agents[1]!.id, agentName: agents[1]!.fullName, workerId: 'W-101', deviceId: device(2), deviceCode: 'POS-202', stationId: station(2), startedAt: at(26), endedAt: at(17), endReason: 'SIGNED_OUT' },
  { id: id('h0000001', 3), agentId: agents[2]!.id, agentName: agents[2]!.fullName, workerId: 'W-102', deviceId: device(3), deviceCode: 'POS-203', stationId: station(3), startedAt: at(52), endedAt: at(44), endReason: 'EXPIRED' },
  { id: id('h0000001', 4), agentId: agents[0]!.id, agentName: agents[0]!.fullName, workerId: 'W-100', deviceId: device(1), deviceCode: 'POS-201', stationId: station(1), startedAt: at(75), endedAt: at(66), endReason: 'SIGNED_OUT' },
];

const syncDevices: S['DeviceSync'][] = devices
  .filter((item) => item.status === 'ACTIVE')
  .map((item) => ({
    deviceId: item.id,
    code: item.code,
    label: item.label,
    operatorId: OPERATOR_ID,
    stationId: item.stationId,
    stationNameAr: '',
    stationNameEn: '',
    lastSyncAt: item.lastSyncAt,
    stale: item.id === device(2),
  }));

const payouts: S['PayoutResponse'][] = [
  { id: id('y0000001', 1), stationId: station(1), operatorId: OPERATOR_ID, amountPiastres: 12_500_000, bankReference: 'CIB-778812', valueDate: '2026-11-16', status: 'POSTED', approvalRequired: false, approvalThresholdPiastres: 5_000_000, bankAccountId: id('b0000001', 1), requestedBy: 'Ops', requestedAt: at(50), postedAt: at(49) },
  { id: id('y0000001', 2), stationId: station(1), operatorId: OPERATOR_ID, amountPiastres: 9_800_000, bankReference: 'CIB-776301', valueDate: '2026-11-09', status: 'POSTED', approvalRequired: false, approvalThresholdPiastres: 5_000_000, bankAccountId: id('b0000001', 1), requestedBy: 'Ops', requestedAt: at(218), postedAt: at(217) },
];

const statement: S['StationStatementEntryResponse'][] = [
  { id: id('e0000009', 1), direction: 'CREDIT', amountPiastres: 73_313, balanceAfterPiastres: 9_125_400, sourceType: 'FUELING', sourceId: id('f0000001', 1), reference: 'FL-2611-4821', postedAt: at(1), fueling: { id: id('f0000001', 1), reference: 'FL-2611-4821', plate: 'ق ط ر 4821', payableHeld: false, holdReleased: false } },
  { id: id('e0000009', 2), direction: 'CREDIT', amountPiastres: 145_000, balanceAfterPiastres: 9_052_087, sourceType: 'FUELING', sourceId: id('f0000001', 7), reference: 'FL-2611-4815', postedAt: at(3), fueling: { id: id('f0000001', 7), reference: 'FL-2611-4815', plate: 'ط ب ع 9076', payableHeld: true, holdReleased: false } },
  { id: id('e0000009', 3), direction: 'DEBIT', amountPiastres: 12_500_000, balanceAfterPiastres: 8_907_087, sourceType: 'PAYOUT', sourceId: payouts[0]!.id, reference: 'CIB-778812', postedAt: at(49), payout: { id: payouts[0]!.id, bankReference: 'CIB-778812', valueDate: '2026-11-16' } },
  { id: id('e0000009', 4), direction: 'CREDIT', amountPiastres: 105_225, balanceAfterPiastres: 21_407_087, sourceType: 'FUELING', sourceId: id('f0000001', 2), reference: 'FL-2611-4820', postedAt: at(52), fueling: { id: id('f0000001', 2), reference: 'FL-2611-4820', plate: 'م ص ن 1902', payableHeld: false, holdReleased: true } },
];

const bank: S['StationBankDetailsResponse'] = {
  active: {
    id: id('b0000001', 1),
    stationId: station(1),
    bankName: 'CIB',
    holderName: 'Misr Fuel Stations S.A.E.',
    accountLast4: '4410',
    accountKind: 'IBAN',
    bankLetterReference: 'BL-2026-0912',
    status: 'ACTIVE',
    requestedAt: at(2000),
    requestedBy: 'Ops',
    decidedAt: at(1990),
  },
};

const tickets: S['TicketResponse'][] = [
  { id: id('k0000002', 1), reference: 'TK-2051', type: 'DEVICE_FAULT', status: 'IN_PROGRESS', subject: 'Printer on POS-202 jams', scopeType: 'OPERATOR', createdAt: at(30), updatedAt: at(2) },
];

const summary = (url: URL): S['StationStatementSummaryResponse'] => ({
  stationId: station(1),
  from: url.searchParams.get('from') ?? at(720),
  to: url.searchParams.get('to') ?? at(0),
  openingPiastres: 21_301_862,
  creditsPiastres: 323_538,
  debitsPiastres: 12_500_000,
  closingPiastres: 9_125_400,
  entries: statement.length,
  now: { stationId: station(1), balancePiastres: 9_125_400, heldPiastres: 145_000, availablePiastres: 8_980_400 },
});

/** Today's activity for one station, others smaller, so the per-station list differs. */
const activity = (url: URL): S['ActivityDay'][] => {
  const stationId = url.searchParams.get('stationId');
  const index = stationId ? Number(stationId.slice(-1)) : 0;
  const scale = [4, 3, 2, 1, 0][index] ?? 1;
  return [
    {
      day: url.searchParams.get('from') ?? '2026-11-18',
      completed: 6 * scale,
      declined: scale > 2 ? 1 : 0,
      offline: scale > 3 ? 1 : 0,
      withConflicts: index === 1 ? 1 : 0,
      alerts: index === 1 ? 1 : 0,
      amountPiastres: 412_500 * scale,
      volumeMl: 236_250 * scale,
    },
  ];
};

const page = <T>(items: T[]) => ({ items });

export async function installStationStub(context: BrowserContext) {
  const table: Array<[RegExp, (url: URL, match: RegExpMatchArray) => unknown]> = [
    [/\/station\/devices$/, (url) => page(devices.filter((item) => !url.searchParams.get('stationId') || item.stationId === url.searchParams.get('stationId')))],
    [/\/station\/agents$/, (url) => page(agents.filter((item) => !url.searchParams.get('stationId') || item.stationId === url.searchParams.get('stationId')))],
    [/\/station\/sync\/devices$/, () => page(syncDevices)],
    [
      /\/station\/shifts$/,
      (url) =>
        page(
          shifts.filter(
            (item) =>
              (!url.searchParams.get('stationId') || item.stationId === url.searchParams.get('stationId')) &&
              (!url.searchParams.get('agentId') || item.agentId === url.searchParams.get('agentId')) &&
              (!url.searchParams.get('deviceId') || item.deviceId === url.searchParams.get('deviceId')),
          ),
        ),
    ],
    [/\/station\/payouts$/, () => page(payouts)],
    [/\/station\/stations\/([^/]+)\/statement\/summary$/, (url) => summary(url)],
    [/\/station\/stations\/([^/]+)\/statement$/, () => page(statement)],
    [/\/station\/stations\/([^/]+)\/bank-account$/, () => bank],
    [/\/station\/activity$/, (url) => activity(url)],
    [/\/station\/tickets$/, () => page(tickets)],
  ];
  await context.route('**/api/v1/station/**', async (route: Route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const url = new URL(route.request().url());
    const path = url.pathname.replace(/^\/api\/v1/, '');
    for (const [pattern, handler] of table) {
      const match = path.match(pattern);
      if (match) return route.fulfill({ json: handler(url, match) });
    }
    return route.fallback();
  });
}
