/**
 * Fictional, deterministic demo data (Egyptian context, bilingual names).
 * Transactions are generated through the real fueling engine so the ledger, balances, limits
 * and cooldowns are consistent with each other.
 */
import { zonedInstant } from '@/domain/calendar';
import type { Dataset } from '@/domain/dataset';
import { recordFuelTransaction, type FuelRequest, type IdFactory } from '@/domain/fueling';
import { litersToMl, poundsToPiastres } from '@/domain/money';
import type {
  Agent,
  Company,
  Department,
  FleetTier,
  FuelTypeId,
  LedgerEntry,
  LimitRule,
  PosDevice,
  RfidTag,
  Station,
  Vehicle,
} from '@/domain/types';
import type { LocalizedText } from '@/lib/locale';

export const DEMO_IDS = {
  company: 'co-nile',
  stationNewCairo: 'st-new-cairo',
  /** The demo station, device and agent (the POS and Station portal perspectives). */
  station: 'st-nasr-city',
  device: 'dev-ns-01',
  agent: 'ag-10427',
  vehicleCorolla: 'veh-abg-4582',
  vehicleFuso: 'veh-smr-1274',
  vehicleVip: 'veh-krm-7710',
  /** Second clear vehicle for the offline story (the main one is in cooldown after the first sale). */
  vehicleOffline: 'veh-lmn-5521',
  /** Tank 50 L, no daily volume rule: used by the presenter's tank-capacity anomaly. */
  vehicleAnomaly: 'veh-nsr-6215',
  vehicleDelta: 'veh-delta-1',
  vehicleSinai: 'veh-sinai-1',
} as const;

/** Demo PIN for every seeded agent (production stores only a salted hash on the device). */
export const DEMO_AGENT_PIN = '123456';

const TIME_ZONE = 'Africa/Cairo';
const TARGET_BALANCE = poundsToPiastres(842_350);
const MID_MONTH_TOPUP = poundsToPiastres(500_000);

/** Small deterministic PRNG (mulberry32) so the demo looks the same after every reset. */
function createRandom(seed: number) {
  let state = seed >>> 0;
  const next = () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  return {
    next,
    int: (min: number, max: number) => min + Math.floor(next() * (max - min + 1)),
    pick: <T>(items: readonly T[]): T => {
      const item = items[Math.floor(next() * items.length)];
      if (item === undefined) throw new Error('pick from empty list');
      return item;
    },
  };
}

const text = (ar: string, en: string): LocalizedText => ({ ar, en });

const PLATE_LETTERS = ['أ', 'ب', 'ج', 'د', 'ر', 'س', 'ص', 'ط', 'ع', 'ف', 'ق', 'ل', 'م', 'ن', 'هـ', 'و', 'ى'];

interface ModelSpec {
  make: LocalizedText;
  model: LocalizedText;
  fuel: FuelTypeId;
  tankLiters: number;
  typicalLiters: [number, number];
}

const MODELS = {
  corolla: { make: text('تويوتا', 'Toyota'), model: text('كورولا', 'Corolla'), fuel: 'gas92', tankLiters: 50, typicalLiters: [25, 42] },
  sunny: { make: text('نيسان', 'Nissan'), model: text('صني', 'Sunny'), fuel: 'gas92', tankLiters: 41, typicalLiters: [20, 36] },
  elantra: { make: text('هيونداي', 'Hyundai'), model: text('إلنترا', 'Elantra'), fuel: 'gas92', tankLiters: 47, typicalLiters: [22, 40] },
  hilux: { make: text('تويوتا', 'Toyota'), model: text('هايلكس', 'Hilux'), fuel: 'gas92', tankLiters: 80, typicalLiters: [35, 65] },
  tucson: { make: text('هيونداي', 'Hyundai'), model: text('توسان', 'Tucson'), fuel: 'gas95', tankLiters: 62, typicalLiters: [30, 55] },
  fuso: { make: text('ميتسوبيشي', 'Mitsubishi'), model: text('فوسو', 'Fuso'), fuel: 'diesel', tankLiters: 200, typicalLiters: [90, 170] },
  npr: { make: text('إيسوزو', 'Isuzu'), model: text('NPR', 'NPR'), fuel: 'diesel', tankLiters: 140, typicalLiters: [70, 120] },
  actros: { make: text('مرسيدس', 'Mercedes'), model: text('أكتروس', 'Actros'), fuel: 'diesel', tankLiters: 300, typicalLiters: [140, 260] },
} satisfies Record<string, ModelSpec>;

type ModelKey = keyof typeof MODELS;

const sequentialIds = (): IdFactory => {
  let counter = 0;
  return { next: () => `seed-${(++counter).toString(36)}` };
};

export function createSeedDataset(now: Date = new Date()): Dataset {
  const random = createRandom(20261012);
  const ids = sequentialIds();

  const companies: Record<string, Company> = {
    'co-nile': {
      id: 'co-nile',
      name: text('النيل للنقل والخدمات', 'Nile Transport & Services'),
      status: 'active',
      cooldownMinutes: 180,
      lowBalanceThresholdPiastres: poundsToPiastres(100_000),
    },
    'co-delta': {
      id: 'co-delta',
      name: text('الدلتا للتوزيع', 'Delta Distribution'),
      status: 'active',
      cooldownMinutes: 120,
      lowBalanceThresholdPiastres: poundsToPiastres(50_000),
    },
    'co-sinai': {
      id: 'co-sinai',
      name: text('سيناء للمقاولات', 'Sinai Contracting'),
      status: 'frozen',
      cooldownMinutes: 240,
      lowBalanceThresholdPiastres: poundsToPiastres(50_000),
    },
  };

  const departments: Record<string, Department> = {
    'dep-heavy': { id: 'dep-heavy', companyId: 'co-nile', name: text('قسم النقل الثقيل', 'Heavy Transport'), monthlyBudgetPiastres: poundsToPiastres(500_000) },
    'dep-distribution': { id: 'dep-distribution', companyId: 'co-nile', name: text('قسم التوزيع', 'Distribution'), monthlyBudgetPiastres: poundsToPiastres(180_000) },
    'dep-admin': { id: 'dep-admin', companyId: 'co-nile', name: text('الإدارة', 'Administration'), monthlyBudgetPiastres: poundsToPiastres(60_000) },
  };

  const stations: Record<string, Station> = {
    [DEMO_IDS.stationNewCairo]: {
      id: DEMO_IDS.stationNewCairo,
      operatorId: 'op-elite',
      name: text('محطة زُود - القاهرة الجديدة', 'ZOOD Station - New Cairo'),
      timeZone: TIME_ZONE,
      connectivity: 'normal',
      prices: { gas92: 1900, gas95: 2100, diesel: 1750 },
    },
    [DEMO_IDS.station]: {
      id: DEMO_IDS.station,
      operatorId: 'op-elite',
      name: text('محطة زُود - مدينة نصر', 'ZOOD Station - Nasr City'),
      timeZone: TIME_ZONE,
      connectivity: 'normal',
      prices: { gas92: 1900, gas95: 2100, diesel: 1750 },
    },
  };

  const minutesAgo = (minutes: number) => new Date(now.getTime() - minutes * 60_000).toISOString();
  const devices: Record<string, PosDevice> = {
    'dev-ns-01': { id: 'dev-ns-01', code: 'POS-NS-01', stationId: DEMO_IDS.station, status: 'online', printer: 'ready', lastSyncAt: minutesAgo(1) },
    'dev-ns-02': { id: 'dev-ns-02', code: 'POS-NS-02', stationId: DEMO_IDS.station, status: 'online', printer: 'ready', lastSyncAt: minutesAgo(3) },
    'dev-ns-03': { id: 'dev-ns-03', code: 'POS-NS-03', stationId: DEMO_IDS.station, status: 'online', printer: 'paperLow', lastSyncAt: minutesAgo(6) },
    'dev-ns-04': { id: 'dev-ns-04', code: 'POS-NS-04', stationId: DEMO_IDS.station, status: 'offline', printer: 'ready', lastSyncAt: minutesAgo(18) },
    'dev-nc-01': { id: 'dev-nc-01', code: 'POS-NC-01', stationId: DEMO_IDS.stationNewCairo, status: 'online', printer: 'ready', lastSyncAt: minutesAgo(2) },
    'dev-nc-02': { id: 'dev-nc-02', code: 'POS-NC-02', stationId: DEMO_IDS.stationNewCairo, status: 'online', printer: 'ready', lastSyncAt: minutesAgo(9) },
  };

  const agents: Record<string, Agent> = {
    'ag-10427': { id: 'ag-10427', workerId: '10427', name: text('محمود عادل', 'Mahmoud Adel'), stationId: DEMO_IDS.station },
    'ag-10431': { id: 'ag-10431', workerId: '10431', name: text('أحمد ربيع', 'Ahmed Rabie'), stationId: DEMO_IDS.stationNewCairo },
    'ag-20112': { id: 'ag-20112', workerId: '20112', name: text('كريم سامي', 'Karim Samy'), stationId: DEMO_IDS.station },
  };

  const vehicles: Record<string, Vehicle> = {};
  const tags: Record<string, RfidTag> = {};
  const usedPlates = new Set<string>();
  const addVehicle = (
    id: string,
    plate: string,
    companyId: string,
    departmentId: string,
    modelKey: ModelKey,
    year: number,
    tier: FleetTier = 'normal',
    status: Vehicle['status'] = 'active',
  ) => {
    const spec = MODELS[modelKey];
    const tagId = `tag-${id}`;
    usedPlates.add(plate);
    vehicles[id] = {
      id,
      companyId,
      departmentId,
      plate,
      make: spec.make,
      model: spec.model,
      year,
      fuelTypeId: spec.fuel,
      tankCapacityMl: litersToMl(spec.tankLiters),
      tier,
      status,
      tagId,
    };
    const hex = () => random.int(16, 255).toString(16).toUpperCase();
    tags[tagId] = { id: tagId, maskedUid: `04:${hex()}:••:••:${hex()}`, vehicleId: id, status: 'active' };
  };

  // Key demo vehicles.
  addVehicle(DEMO_IDS.vehicleCorolla, 'أ ب ج 4582', 'co-nile', 'dep-distribution', 'corolla', 2024);
  addVehicle(DEMO_IDS.vehicleFuso, 'س م ر 1274', 'co-nile', 'dep-heavy', 'fuso', 2022);
  addVehicle('veh-dhw-3906', 'د هـ و 3906', 'co-nile', 'dep-heavy', 'npr', 2021);
  addVehicle(DEMO_IDS.vehicleOffline, 'ل م ن 5521', 'co-nile', 'dep-distribution', 'sunny', 2023);
  addVehicle(DEMO_IDS.vehicleVip, 'ك ر م 7710', 'co-nile', 'dep-admin', 'tucson', 2023, 'vip');
  addVehicle(DEMO_IDS.vehicleAnomaly, 'ن ص ر 6215', 'co-nile', 'dep-admin', 'corolla', 2022);

  // Rest of the Nile fleet (47 vehicles in total).
  const fleetPlan: Array<[string, ModelKey[], number]> = [
    ['dep-heavy', ['fuso', 'npr', 'actros'], 16],
    ['dep-distribution', ['sunny', 'elantra', 'hilux', 'corolla'], 22],
    ['dep-admin', ['corolla', 'elantra', 'tucson'], 3],
  ];
  let generated = 0;
  for (const [departmentId, models, count] of fleetPlan) {
    for (let index = 0; index < count; index++) {
      let plate: string;
      do {
        const letters = [random.pick(PLATE_LETTERS), random.pick(PLATE_LETTERS), random.pick(PLATE_LETTERS)];
        plate = `${letters.join(' ')} ${random.int(1000, 9899)}`;
      } while (usedPlates.has(plate));
      const status = generated === 7 || generated === 23 ? 'suspended' : 'active';
      addVehicle(`veh-gen-${++generated}`, plate, 'co-nile', departmentId, random.pick(models), random.int(2019, 2025), 'normal', status);
    }
  }
  // A couple of vehicles for the other companies (Operations views).
  departments['dep-delta-main'] = { id: 'dep-delta-main', companyId: 'co-delta', name: text('الأسطول الرئيسي', 'Main fleet'), monthlyBudgetPiastres: poundsToPiastres(120_000) };
  departments['dep-sinai-main'] = { id: 'dep-sinai-main', companyId: 'co-sinai', name: text('المعدات', 'Equipment'), monthlyBudgetPiastres: poundsToPiastres(90_000) };
  addVehicle('veh-delta-1', 'ط ق ل 2290', 'co-delta', 'dep-delta-main', 'hilux', 2022);
  addVehicle('veh-delta-2', 'ن ف ع 6143', 'co-delta', 'dep-delta-main', 'npr', 2020);
  addVehicle('veh-sinai-1', 'ص و ر 8051', 'co-sinai', 'dep-sinai-main', 'actros', 2019);

  const nileVehicles = Object.values(vehicles).filter((vehicle) => vehicle.companyId === 'co-nile');
  const byDepartment = (departmentId: string) => nileVehicles.filter((vehicle) => vehicle.departmentId === departmentId);
  const limitRules: LimitRule[] = [
    {
      id: 'limit-car-daily',
      companyId: 'co-nile',
      kind: 'volume',
      period: 'day',
      maxValue: litersToMl(50),
      vehicleIds: [...byDepartment('dep-distribution'), ...byDepartment('dep-admin')]
        .filter((vehicle) => vehicle.tier === 'normal' && vehicle.tankCapacityMl <= litersToMl(62) && vehicle.id !== DEMO_IDS.vehicleAnomaly)
        .map((vehicle) => vehicle.id),
    },
    {
      id: 'limit-truck-daily',
      companyId: 'co-nile',
      kind: 'volume',
      period: 'day',
      maxValue: litersToMl(200),
      vehicleIds: byDepartment('dep-heavy').map((vehicle) => vehicle.id),
    },
    {
      id: 'limit-pickup-weekly',
      companyId: 'co-nile',
      kind: 'amount',
      period: 'week',
      maxValue: poundsToPiastres(3_000),
      vehicleIds: nileVehicles.filter((vehicle) => vehicle.model.en === 'Hilux').map((vehicle) => vehicle.id),
    },
  ];

  const ledger: LedgerEntry[] = [];
  const wallets: Dataset['wallets'] = {};
  const openWallet = (companyId: string, openingPiastres: number, at: Date) => {
    const walletId = `wal-${companyId}`;
    wallets[companyId] = { id: walletId, companyId, balancePiastres: openingPiastres };
    ledger.push({
      id: ids.next(),
      walletId,
      companyId,
      type: 'topup',
      amountPiastres: openingPiastres,
      balanceAfterPiastres: openingPiastres,
      reference: `BT-${random.int(100000, 999999)}`,
      at: at.toISOString(),
    });
  };
  const monthStart = zonedInstant(now, 31, 9, 15, TIME_ZONE);
  // Provisional opening balance; adjusted below so the final balance matches the demo target.
  openWallet('co-nile', poundsToPiastres(2_000_000), monthStart);
  openWallet('co-delta', poundsToPiastres(46_800), monthStart);
  openWallet('co-sinai', poundsToPiastres(12_400), monthStart);

  let data: Dataset = {
    fuelTypes: {
      gas92: { id: 'gas92', name: text('بنزين 92', 'Gasoline 92') },
      gas95: { id: 'gas95', name: text('بنزين 95', 'Gasoline 95') },
      diesel: { id: 'diesel', name: text('سولار', 'Diesel') },
    },
    companies,
    wallets,
    ledger,
    departments,
    vehicles,
    tags,
    limitRules,
    operators: { 'op-elite': { id: 'op-elite', name: text('النخبة لإدارة المحطات', 'Elite Station Management') } },
    stations,
    devices,
    agents,
    transactions: [],
    alerts: [],
    sequence: 48_000,
  };

  // Plan fuelings over the last 30 days.
  interface PlannedFueling {
    request: FuelRequest;
    processAt: Date;
  }
  const planned: PlannedFueling[] = [];
  const stationAgents: Record<string, string[]> = {
    [DEMO_IDS.stationNewCairo]: ['ag-10431'],
    [DEMO_IDS.station]: ['ag-10427', 'ag-20112'],
  };
  const stationDevices: Record<string, string[]> = {
    [DEMO_IDS.stationNewCairo]: ['dev-nc-01', 'dev-nc-02'],
    [DEMO_IDS.station]: ['dev-ns-01', 'dev-ns-02', 'dev-ns-03'],
  };
  const activeFleet = nileVehicles.filter((vehicle) => vehicle.status === 'active');

  const plan = (vehicle: Vehicle, at: Date, liters: number, origin: FuelRequest['origin'] = 'online', exact = false) => {
    const stationId = random.next() < 0.6 ? DEMO_IDS.station : DEMO_IDS.stationNewCairo;
    const delayMinutes = origin === 'offline' ? random.int(20, 90) : 0;
    planned.push({
      processAt: new Date(at.getTime() + delayMinutes * 60_000),
      request: {
        clientTxnId: ids.next(),
        tagId: vehicle.tagId,
        stationId,
        deviceId: random.pick(stationDevices[stationId] ?? []),
        agentId: random.pick(stationAgents[stationId] ?? []),
        volumeMl: liters * 1000 + (exact ? 0 : random.int(0, 99) * 10),
        origin,
        deviceAt: at.toISOString(),
        receiptLocale: 'ar',
      },
    });
  };

  for (let daysAgo = 30; daysAgo >= 0; daysAgo--) {
    const count = daysAgo === 0 ? random.int(9, 14) : random.int(14, 22);
    const used = new Set<string>();
    for (let index = 0; index < count; index++) {
      const vehicle = random.pick(activeFleet);
      if (used.has(vehicle.id)) continue;
      // Keep the hero vehicles' state predictable for the live demo.
      const heroes: string[] = [DEMO_IDS.vehicleCorolla, DEMO_IDS.vehicleFuso, DEMO_IDS.vehicleAnomaly, DEMO_IDS.vehicleOffline];
      if (daysAgo === 0 && heroes.includes(vehicle.id)) continue;
      used.add(vehicle.id);
      const at = zonedInstant(now, daysAgo, random.int(6, 21), random.int(0, 59), TIME_ZONE);
      if (at > now) continue;
      const spec = Object.values(MODELS).find((model) => model.model.en === vehicle.model.en) ?? MODELS.corolla;
      const liters = random.int(spec.typicalLiters[0], spec.typicalLiters[1]);
      plan(vehicle, at, liters, random.next() < 0.06 ? 'offline' : 'online');
    }
  }
  // Hero vehicle: last fueled two days ago, so the live demo starts with a clear tag and limit.
  const corolla = vehicles[DEMO_IDS.vehicleCorolla];
  if (corolla) plan(corolla, zonedInstant(now, 2, 17, 40, TIME_ZONE), 38, 'online', true);
  // Fuso: 180 L earlier today (outside cooldown) so a further 40 L exceeds its 200 L daily limit.
  const fuso = vehicles[DEMO_IDS.vehicleFuso];
  // Before ~03:05 local time there is no 'earlier today' outside the 3 h cooldown; the fueling is then
  // placed at 00:05 and the tag cooldown is cleared below (demo preset, documented in overnight-decisions).
  const startOfToday = zonedInstant(now, 0, 0, 5, TIME_ZONE);
  const fourHoursAgo = new Date(now.getTime() - 4 * 3600_000);
  const fusoAt = fourHoursAgo > startOfToday ? fourHoursAgo : startOfToday;
  if (fuso && fusoAt < now) plan(fuso, fusoAt, 180, 'online', true);

  planned.sort((a, b) => a.processAt.getTime() - b.processAt.getTime());
  const midMonthTopupAt = zonedInstant(now, 11, 10, 30, TIME_ZONE);
  let topupDone = false;
  for (const { request, processAt } of planned) {
    if (!topupDone && processAt > midMonthTopupAt) {
      data = creditWallet(data, 'co-nile', MID_MONTH_TOPUP, midMonthTopupAt, `BT-${random.int(100000, 999999)}`, ids);
      topupDone = true;
    }
    data = recordFuelTransaction(data, request, processAt, ids).data;
  }
  if (!topupDone) data = creditWallet(data, 'co-nile', MID_MONTH_TOPUP, midMonthTopupAt, 'BT-500000', ids);

  data = adjustOpeningBalance(data, 'co-nile', TARGET_BALANCE);

  // Demo presets: the hero tags start clear so the live scenarios are deterministic.
  for (const vehicleId of [DEMO_IDS.vehicleCorolla, DEMO_IDS.vehicleFuso, DEMO_IDS.vehicleAnomaly, DEMO_IDS.vehicleOffline]) {
    const tagId = `tag-${vehicleId}`;
    const tag = data.tags[tagId];
    if (tag) data.tags = { ...data.tags, [tagId]: { ...tag, cooldownUntil: undefined } };
  }

  data.alerts = [
    ...data.alerts,
    { id: ids.next(), type: 'limitReached', severity: 'warning', status: 'open', at: fusoAt.toISOString(), companyId: 'co-nile', vehicleId: DEMO_IDS.vehicleFuso },
    { id: ids.next(), type: 'plateUnverified', severity: 'warning', status: 'open', at: minutesAgo(26 * 60), companyId: 'co-nile', vehicleId: 'veh-dhw-3906', stationId: DEMO_IDS.stationNewCairo },
    { id: ids.next(), type: 'deviceOffline', severity: 'warning', status: 'open', at: minutesAgo(18), stationId: DEMO_IDS.station, deviceId: 'dev-ns-04' },
    { id: ids.next(), type: 'lowBalance', severity: 'critical', status: 'open', at: minutesAgo(5 * 60), companyId: 'co-delta' },
  ];
  return data;
}

function creditWallet(data: Dataset, companyId: string, amount: number, at: Date, reference: string, ids: IdFactory): Dataset {
  const wallet = data.wallets[companyId];
  if (!wallet) return data;
  const balanceAfterPiastres = wallet.balancePiastres + amount;
  const entry: LedgerEntry = {
    id: ids.next(),
    walletId: wallet.id,
    companyId,
    type: 'topup',
    amountPiastres: amount,
    balanceAfterPiastres,
    reference,
    at: at.toISOString(),
  };
  return {
    ...data,
    wallets: { ...data.wallets, [companyId]: { ...wallet, balancePiastres: balanceAfterPiastres } },
    ledger: [...data.ledger, entry],
  };
}

/** Shifts the opening top-up (and every running balance) so the wallet ends at the target balance. */
function adjustOpeningBalance(data: Dataset, companyId: string, target: number): Dataset {
  const wallet = data.wallets[companyId];
  if (!wallet) return data;
  const delta = target - wallet.balancePiastres;
  let openingAdjusted = false;
  const ledger = data.ledger.map((entry) => {
    if (entry.companyId !== companyId) return entry;
    const adjusted = { ...entry, balanceAfterPiastres: entry.balanceAfterPiastres + delta };
    if (!openingAdjusted) {
      openingAdjusted = true;
      adjusted.amountPiastres += delta;
    }
    return adjusted;
  });
  return { ...data, ledger, wallets: { ...data.wallets, [companyId]: { ...wallet, balancePiastres: target } } };
}
