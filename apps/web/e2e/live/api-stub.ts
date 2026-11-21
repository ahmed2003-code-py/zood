import type { BrowserContext, Route } from '@playwright/test';
import type { components } from '../../src/api/generated/schema';

/**
 * A small, fixed API world for looking at the live screens (VITE_API_MODE=http) without a server.
 * It answers the read endpoints the key screens call with typed fixtures, so a screenshot is the
 * same on every run. It is for visual baselines only: writes answer an empty success.
 */
type S = components['schemas'];
export type StubRole = 'company' | 'station' | 'ops';

const DAY = 86_400_000;
const NOW = Date.parse('2026-11-18T09:30:00Z');
const at = (daysAgo: number, hours = 0) => new Date(NOW - daysAgo * DAY + hours * 3_600_000).toISOString();
const id = (prefix: string, n: number) => `${prefix}-0000-7000-8000-${String(n).padStart(12, '0')}`;

const COMPANY_ID = id('c0000001', 1);
const OPERATOR_ID = id('o0000001', 1);

const companies: S['CompanyResponse'][] = [
  ['النيل للنقل', 'Nile Transport', 'ACTIVE', 'Nile Transport S.A.E.'],
  ['الدلتا للخدمات اللوجستية', 'Delta Logistics', 'ACTIVE', 'Delta Logistics LLC'],
  ['القاهرة للتوزيع', 'Cairo Distribution', 'FROZEN', 'Cairo Distribution Co.'],
  ['الإسكندرية للشحن', 'Alexandria Freight', 'ACTIVE', 'Alexandria Freight S.A.E.'],
  ['الصعيد للمقاولات', 'Upper Egypt Contracting', 'ACTIVE', 'Upper Egypt Contracting'],
].map(([nameAr, nameEn, status, legalName], index) => ({
  id: index === 0 ? COMPANY_ID : id('c0000001', index + 1),
  nameAr,
  nameEn,
  legalName,
  status: status as 'ACTIVE' | 'FROZEN',
  cooldownMinutes: 30,
  lowBalanceThresholdPiastres: 5_000_000,
  commercialRegistration: `CR-${10240 + index}`,
  taxId: `TX-${550_100 + index}`,
  contactName: 'Ahmed Salah',
  contactEmail: `fleet${index + 1}@example.com`,
  contactPhone: `+20100000${1000 + index}`,
  createdAt: at(120 - index * 9),
}));

const departments: S['DepartmentResponse'][] = [
  { id: id('d0000001', 1), name: 'التوزيع', active: true, monthlyBudgetPiastres: 25_000_000 },
  { id: id('d0000001', 2), name: 'المبيعات', active: true, monthlyBudgetPiastres: 12_000_000 },
  { id: id('d0000001', 3), name: 'الإدارة', active: true },
];

const PLATES = ['ق ط ر 4821', 'م ص ن 1902', 'أ ب ج 7733', 'س ع د 2210', 'ل ك م 6045', 'ه و ي 3318', 'ط ب ع 9076', 'ر س ل 5520'];
const MAKES: Array<[string, string, S['VehicleResponse']['fuelType']]> = [
  ['Toyota', 'Hilux', 'GAS_92'],
  ['Mitsubishi', 'Canter', 'DIESEL'],
  ['Hyundai', 'H100', 'DIESEL'],
  ['Nissan', 'Sunny', 'GAS_92'],
  ['Isuzu', 'NPR', 'DIESEL'],
  ['Chevrolet', 'N300', 'GAS_92'],
  ['Kia', 'K2700', 'DIESEL'],
  ['Toyota', 'Corolla', 'GAS_95'],
];
const vehicles: S['VehicleResponse'][] = PLATES.map((plate, index) => ({
  id: id('v0000001', index + 1),
  companyId: COMPANY_ID,
  departmentId: departments[index % 3]!.id,
  plate,
  fleetCode: `NT-${String(index + 101)}`,
  make: MAKES[index]![0],
  model: MAKES[index]![1],
  fuelType: MAKES[index]![2],
  year: 2019 + (index % 6),
  tankCapacityMl: [80_000, 100_000, 75_000, 50_000, 120_000, 60_000, 70_000, 55_000][index]!,
  tier: index === 7 ? 'VIP' : 'NORMAL',
  vipMonthlyCeilingMl: index === 7 ? 400_000 : undefined,
  status: index === 4 ? 'SUSPENDED' : 'ACTIVE',
  createdAt: at(100 - index),
}));

const stations: S['StationResponse'][] = [
  ['محطة التجمع الخامس', 'Fifth Settlement Station', 'NORMAL', 'ACTIVE'],
  ['محطة مدينة نصر', 'Nasr City Station', 'NORMAL', 'ACTIVE'],
  ['محطة طريق السويس', 'Suez Road Station', 'LOW', 'ACTIVE'],
  ['محطة الشيخ زايد', 'Sheikh Zayed Station', 'NORMAL', 'SUSPENDED'],
].map(([nameAr, nameEn, connectivity, status], index) => ({
  id: id('s0000001', index + 1),
  operatorId: OPERATOR_ID,
  nameAr,
  nameEn,
  connectivity: connectivity as 'NORMAL' | 'LOW',
  status: status as 'ACTIVE' | 'SUSPENDED',
  fuelTypes: ['GAS_92', 'GAS_95', 'DIESEL'],
  address: `${10 + index} Road ${90 + index}, Cairo`,
  timeZone: 'Africa/Cairo',
  createdAt: at(200 - index),
}));

const PRICE: Record<S['VehicleResponse']['fuelType'], number> = { GAS_80: 1550, GAS_92: 1725, GAS_95: 1900, DIESEL: 1550, CNG: 700 };
const fuelings: S['FuelingResponse'][] = Array.from({ length: 14 }, (_, index) => {
  const vehicle = vehicles[index % vehicles.length]!;
  const station = stations[index % 3]!;
  const declined = index === 3 || index === 10;
  const volumeMl = declined ? 0 : [42_500, 61_000, 35_250, 0, 88_000, 47_750, 52_000, 30_500, 73_250, 40_000, 0, 66_500, 39_000, 58_750][index]!;
  const unitPricePiastres = PRICE[vehicle.fuelType];
  return {
    id: id('f0000001', index + 1),
    reference: `FL-2611-${String(4821 - index)}`,
    companyId: COMPANY_ID,
    companyNameAr: companies[0]!.nameAr,
    companyNameEn: companies[0]!.nameEn,
    vehicleId: vehicle.id,
    plate: vehicle.plate,
    stationId: station.id,
    stationNameAr: station.nameAr,
    stationNameEn: station.nameEn,
    operatorId: OPERATOR_ID,
    deviceId: id('p0000001', (index % 3) + 1),
    agentWorkerId: `W-${100 + (index % 4)}`,
    fuelType: vehicle.fuelType,
    volumeMl,
    unitPricePiastres,
    amountPiastres: Math.round((volumeMl * unitPricePiastres) / 1000),
    status: declined ? 'DECLINED' : 'COMPLETED',
    declineReason: index === 3 ? 'LIMIT_EXCEEDED' : index === 10 ? 'COOLDOWN' : undefined,
    origin: index === 6 ? 'OFFLINE' : 'ONLINE',
    billable: !declined,
    conflicts: index === 6 ? ['STALE_CAPTURE'] : [],
    deviceAt: at(Math.floor(index / 3), -index),
    evaluatedAt: at(Math.floor(index / 3), -index),
    receivedAt: at(Math.floor(index / 3), -index),
  };
});

const wallet: S['WalletResponse'] = {
  companyId: COMPANY_ID,
  balancePiastres: 18_452_075,
  lowBalance: false,
  lowBalanceThresholdPiastres: 5_000_000,
  updatedAt: at(0, -1),
};

let balance = wallet.balancePiastres;
const statement: S['StatementEntryResponse'][] = fuelings
  .filter((fueling) => fueling.status === 'COMPLETED')
  .slice(0, 8)
  .flatMap((fueling, index) => {
    const rows: S['StatementEntryResponse'][] = [];
    if (index === 3) {
      rows.push({
        id: id('e0000002', index),
        direction: 'CREDIT',
        amountPiastres: 10_000_000,
        balanceAfterPiastres: balance,
        sourceType: 'TOPUP',
        sourceId: id('t0000001', 1),
        reference: 'TRX-88213',
        postedAt: at(1, 2),
      });
      balance -= 10_000_000;
    }
    rows.push({
      id: id('e0000001', index),
      direction: 'DEBIT',
      amountPiastres: fueling.amountPiastres,
      balanceAfterPiastres: balance,
      sourceType: 'FUELING',
      sourceId: fueling.id,
      reference: fueling.reference,
      postedAt: fueling.receivedAt,
    });
    balance += fueling.amountPiastres;
    return rows;
  });

const topUps: S['TopUpResponse'][] = [
  ['PENDING', 15_000_000, 0],
  ['PENDING_APPROVAL', 40_000_000, 1],
  ['CONFIRMED', 10_000_000, 2],
  ['REJECTED', 2_500_000, 6],
].map(([status, amountPiastres, daysAgo], index) => ({
  id: id('t0000001', index + 1),
  companyId: companies[index % 2]!.id,
  amountPiastres: amountPiastres as number,
  verifiedAmountPiastres: status === 'PENDING' ? undefined : (amountPiastres as number),
  bankReference: `TRX-${88_210 + index}`,
  receivingAccount: 'CIB •••• 4410',
  transferDate: at(daysAgo as number).slice(0, 10),
  origin: 'COMPANY_REQUEST',
  status: status as S['TopUpResponse']['status'],
  requestedAt: at(daysAgo as number),
  requestedBy: 'Ahmed Salah',
  decisionReason: status === 'REJECTED' ? 'Transfer not found on the statement' : undefined,
  decidedAt: status === 'CONFIRMED' || status === 'REJECTED' ? at((daysAgo as number) - 1) : undefined,
}));

const users: S['AdminUserResponse'][] = [
  ['سارة منصور', 'sara@zawed.example', 'SUPER_ADMIN', 'PLATFORM', 'ACTIVE'],
  ['محمد عادل', 'm.adel@zawed.example', 'OPS_MANAGER', 'PLATFORM', 'ACTIVE'],
  ['منى حسن', 'mona@zawed.example', 'OPS', 'PLATFORM', 'ACTIVE'],
  ['أحمد صلاح', 'ahmed@niletransport.example', 'FLEET_MANAGER', 'COMPANY', 'ACTIVE'],
  ['ياسمين فؤاد', 'yasmin@niletransport.example', 'ACCOUNTANT', 'COMPANY', 'SUSPENDED'],
  ['كريم نبيل', 'karim@fuelco.example', 'STATION_MANAGER', 'OPERATOR', 'ACTIVE'],
].map(([fullName, email, role, scope, status], index) => ({
  id: id('u0000001', index + 1),
  fullName,
  email,
  role: role as S['AdminUserResponse']['role'],
  scope: { type: scope as 'PLATFORM' | 'COMPANY' | 'OPERATOR', id: scope === 'COMPANY' ? COMPANY_ID : scope === 'OPERATOR' ? OPERATOR_ID : undefined },
  status: status as S['AdminUserResponse']['status'],
  mfaEnabled: index < 3,
  mustChangePassword: index === 5,
  createdAt: at(90 - index * 5),
}));

const alerts: S['AlertResponse'][] = [
  ['TANK_CAPACITY_EXCEEDED', 'CRITICAL', 'OPEN'],
  ['LOW_BALANCE', 'WARNING', 'OPEN'],
  ['SYNC_CONFLICT', 'WARNING', 'ACKNOWLEDGED'],
].map(([type, severity, status], index) => ({
  id: id('a0000001', index + 1),
  type: type as S['AlertResponse']['type'],
  severity: severity as 'WARNING' | 'CRITICAL',
  status: status as 'OPEN' | 'ACKNOWLEDGED',
  companyId: COMPANY_ID,
  vehicleId: vehicles[index]!.id,
  fuelingId: fuelings[index]!.id,
  stationId: stations[0]!.id,
  details: { volumeMl: 88_000, tankCapacityMl: 80_000, plate: vehicles[index]!.plate },
  createdAt: at(index, -2),
}));

const tickets: S['TicketResponse'][] = [
  ['LOST_TAG', 'OPEN', 'Lost tag on a Canter truck'],
  ['TOPUP_QUERY', 'IN_PROGRESS', 'Top-up from Monday not showing yet'],
  ['LIMIT_CHANGE', 'RESOLVED', 'Raise the weekly limit for the sales team'],
].map(([type, status, subject], index) => ({
  id: id('k0000001', index + 1),
  reference: `TK-${1040 + index}`,
  type: type as S['TicketResponse']['type'],
  status: status as S['TicketResponse']['status'],
  subject,
  scopeType: 'COMPANY',
  companyId: COMPANY_ID,
  createdAt: at(index * 2 + 1),
  updatedAt: at(index),
}));

const operator: S['OperatorResponse'] = {
  id: OPERATOR_ID,
  nameAr: 'وقود مصر',
  nameEn: 'Misr Fuel',
  legalName: 'Misr Fuel Stations S.A.E.',
  status: 'ACTIVE',
  createdAt: at(300),
};

const devices: S['DeviceResponse'][] = stations.slice(0, 3).map((station, index) => ({
  id: id('p0000001', index + 1),
  code: `POS-${201 + index}`,
  label: `Pump island ${index + 1}`,
  operatorId: OPERATOR_ID,
  stationId: station.id,
  status: index === 2 ? 'PENDING_ACTIVATION' : 'ACTIVE',
  activationExpired: false,
  lastSyncAt: index === 2 ? undefined : at(0, -index),
  createdAt: at(60),
}));

const agents: S['AgentResponse'][] = ['محمود سعيد', 'عمر جمال', 'حسن علي'].map((fullName, index) => ({
  id: id('g0000001', index + 1),
  fullName,
  workerId: `W-${100 + index}`,
  operatorId: OPERATOR_ID,
  stationId: stations[index]!.id,
  status: 'ACTIVE',
  mustChangePin: false,
  locked: false,
  createdAt: at(50),
}));

const activity: S['ActivityDay'][] = Array.from({ length: 14 }, (_, index) => ({
  day: at(13 - index).slice(0, 10),
  completed: 18 + ((index * 7) % 11),
  declined: index % 4 === 0 ? 2 : 0,
  offline: index % 5 === 0 ? 1 : 0,
  withConflicts: index % 6 === 0 ? 1 : 0,
  alerts: index % 3 === 0 ? 1 : 0,
  amountPiastres: 1_600_000 + ((index * 312_500) % 900_000),
  volumeMl: 900_000 + ((index * 170_000) % 500_000),
}));

const payable: S['StationPayableResponse'] = {
  stationId: stations[0]!.id,
  balancePiastres: 9_125_400,
  heldPiastres: 145_000,
  availablePiastres: 8_980_400,
  pendingPayoutsPiastres: 0,
};

const page = <T>(items: T[]) => ({ items });

const USERS: Record<StubRole, S['UserResponse']> = {
  company: { id: users[3]!.id, fullName: 'أحمد صلاح', email: users[3]!.email, role: 'FLEET_MANAGER', scope: { type: 'COMPANY', id: COMPANY_ID }, mfaEnabled: false, mustChangePassword: false },
  station: { id: users[5]!.id, fullName: 'كريم نبيل', email: users[5]!.email, role: 'STATION_MANAGER', scope: { type: 'OPERATOR', id: OPERATOR_ID }, mfaEnabled: false, mustChangePassword: false },
  ops: { id: users[0]!.id, fullName: 'سارة منصور', email: users[0]!.email, role: 'SUPER_ADMIN', scope: { type: 'PLATFORM' }, mfaEnabled: true, mustChangePassword: false },
};

type Handler = (match: RegExpMatchArray, url: URL) => unknown;

function routes(role: StubRole): Array<[RegExp, Handler]> {
  const vehicleDetail = (vehicleId: string): S['VehicleDetail'] => {
    const vehicle = vehicles.find((item) => item.id === vehicleId) ?? vehicles[0]!;
    return {
      vehicle,
      tags: [{ id: id('r0000001', 1), vehicleId: vehicle.id, uidLast4: '9F2A', status: 'ACTIVE', createdAt: at(80) }],
      limits: [
        { id: id('l0000001', 1), kind: 'VOLUME', period: 'DAY', maxValue: 120_000, createdAt: at(80) },
        { id: id('l0000001', 2), kind: 'AMOUNT', period: 'MONTH', maxValue: 5_000_000, createdAt: at(80) },
      ],
    };
  };
  const fueling = (fuelingId: string) => fuelings.find((item) => item.id === fuelingId) ?? fuelings[0]!;
  const thread = (ticketId: string): S['TicketThreadResponse'] => ({
    ticket: tickets.find((item) => item.id === ticketId) ?? tickets[0]!,
    messages: [
      { id: id('m0000001', 1), authorRole: 'FLEET_MANAGER', body: 'The tag on truck NT-102 fell off at the depot.', createdAt: at(2), internal: false },
      { id: id('m0000001', 2), authorRole: 'OPS', body: 'Thanks. The old tag is blocked; a new one goes out tomorrow.', createdAt: at(1), internal: false },
    ],
  });
  const onboarding: S['OnboardingResponse'] = {
    companyId: COMPANY_ID,
    ready: false,
    checks: [
      { key: 'ACTIVE', done: true, required: true },
      { key: 'CONTACT', done: true, required: true },
      { key: 'DEPARTMENTS', done: true, required: true, count: 3 },
      { key: 'VEHICLES', done: true, required: true, count: 8 },
      { key: 'TAGS', done: false, required: true, count: 6 },
      { key: 'WALLET_FUNDED', done: false, required: true },
      { key: 'COMPANY_USERS', done: true, required: false, count: 2 },
    ],
  };
  const common: Array<[RegExp, Handler]> = [
    [/\/auth\/me$/, () => USERS[role]],
    [/\/(company|station|ops)\/fuelings\/([^/]+)$/, (m) => fueling(m[2]!)],
    [/\/(company|station|ops)\/fuelings$/, () => page(fuelings)],
    [/\/(company|station|ops)\/activity$/, () => activity],
    [/\/(company|station|ops)\/tickets\/([^/]+)$/, (m) => thread(m[2]!)],
    [/\/(company|station|ops)\/tickets$/, () => page(tickets)],
    [/\/(company|station|ops)\/reversals$/, () => page([])],
    [/\/(company|ops)\/alerts$/, () => page(alerts)],
  ];
  const company: Array<[RegExp, Handler]> = [
    [/\/company\/profile$/, () => companies[0]],
    [/\/company\/departments$/, () => page(departments)],
    [/\/company\/vehicles\/([^/]+)$/, (m) => vehicleDetail(m[1]!)],
    [/\/company\/vehicles$/, () => page(vehicles)],
    [/\/company\/wallet\/statement$/, () => page(statement)],
    [/\/company\/wallet$/, () => wallet],
    [/\/company\/topups$/, () => page(topUps.slice(0, 3).map((item) => ({ ...item, companyId: COMPANY_ID })))],
  ];
  const station: Array<[RegExp, Handler]> = [
    [/\/station\/operator$/, () => operator],
    [/\/station\/stations\/([^/]+)\/payable$/, () => payable],
    [/\/station\/stations\/([^/]+)\/statement$/, () => page([])],
    [/\/station\/stations\/([^/]+)\/bank-account$/, () => ({})],
    [/\/station\/stations\/([^/]+)$/, (m) => stations.find((item) => item.id === m[1]) ?? stations[0]],
    [/\/station\/stations$/, () => page(stations)],
    [/\/station\/devices$/, () => page(devices)],
    [/\/station\/agents$/, () => page(agents)],
    [/\/station\/payouts$/, () => page([])],
    [/\/station\/shifts$/, () => page([])],
  ];
  const ops: Array<[RegExp, Handler]> = [
    [/\/ops\/companies\/([^/]+)\/onboarding$/, () => onboarding],
    [/\/ops\/companies\/([^/]+)\/departments$/, () => page(departments)],
    [/\/ops\/companies\/([^/]+)\/wallet\/statement$/, () => page(statement)],
    [/\/ops\/companies\/([^/]+)\/wallet$/, () => wallet],
    [/\/ops\/companies\/([^/]+)$/, (m) => companies.find((item) => item.id === m[1]) ?? companies[0]],
    [/\/ops\/companies$/, () => page(companies)],
    [/\/ops\/vehicles\/([^/]+)$/, (m) => vehicleDetail(m[1]!)],
    [/\/ops\/vehicles$/, () => page(vehicles)],
    [/\/ops\/topups\/receiving-accounts$/, () => ['CIB •••• 4410', 'NBE •••• 1822']],
    [/\/ops\/topups$/, () => page(topUps)],
    [/\/ops\/wallet-adjustments$/, () => page([])],
    [/\/ops\/fleet-imports$/, () => page([])],
    [/\/ops\/operators\/([^/]+)$/, () => operator],
    [/\/ops\/operators$/, () => page([operator])],
    [/\/ops\/stations\/([^/]+)\/payable$/, () => payable],
    [/\/ops\/stations\/([^/]+)\/statement$/, () => page([])],
    [/\/ops\/stations\/([^/]+)\/bank-account$/, () => ({})],
    [/\/ops\/stations\/([^/]+)$/, (m) => stations.find((item) => item.id === m[1]) ?? stations[0]],
    [/\/ops\/stations$/, () => page(stations)],
    [/\/ops\/devices$/, () => page(devices)],
    [/\/ops\/agents$/, () => page(agents)],
    [/\/ops\/prices\/current$/, () => (['GAS_92', 'GAS_95', 'DIESEL'] as const).map((fuelType) => ({ fuelType, pricePiastresPerLitre: PRICE[fuelType], effectiveFrom: at(30) }))],
    [/\/ops\/prices$/, () => page([])],
    [/\/ops\/(payouts|payable-holds|bank-account-changes|shifts|sync\/devices|sync\/batches|reconciliation\/runs)$/, () => page([])],
    [/\/admin\/users$/, () => page(users)],
    [/\/admin\/audit$/, () => page([])],
  ];
  return [...common, ...(role === 'company' ? company : role === 'station' ? station : ops)];
}

/** Signs the context in as the role's user and answers /api/v1 from the fixtures. */
export async function installApiStub(context: BrowserContext, role: StubRole, baseURL: string) {
  const door = role === 'ops' ? 'admin' : 'user';
  await context.addCookies([{ name: 'zawed_session', value: '1', url: baseURL }]);
  await context.addInitScript((value) => localStorage.setItem('zawed.auth.door', value), door);
  const table = routes(role);
  const session: S['SessionResponse'] = { accessToken: 'stub-token', expiresInSeconds: 3600, user: USERS[role] };
  await context.route('**/api/v1/**', async (route: Route) => {
    const url = new URL(route.request().url());
    const path = url.pathname.replace(/^\/api\/v1/, '');
    if (/\/auth\/refresh$/.test(path)) return route.fulfill({ json: session });
    if (route.request().method() !== 'GET') return route.fulfill({ status: 204 });
    for (const [pattern, handler] of table) {
      const match = path.match(pattern);
      if (match) return route.fulfill({ json: handler(match, url) });
    }
    return route.fulfill({ status: 404, json: { code: 'NOT_FOUND', message: `stub: ${path}` } });
  });
}

export const STUB_IDS = { company: COMPANY_ID, vehicle: vehicles[0]!.id, station: stations[0]!.id, fueling: fuelings[0]!.id, ticket: tickets[0]!.id };
