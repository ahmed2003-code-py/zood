/**
 * Creates the demo's master data through the real API (B2 exit criterion): companies, departments,
 * vehicles with their RFID tags and limits, the station operator, stations, POS devices, agents and
 * fuel prices. The data comes from the same createSeedDataset() the mock demo uses, so both show
 * the same world. Wallets and transactions are not master data; they follow in B3 and B4.
 *
 *   ZAWED_API_URL=http://localhost:8080 ZAWED_ACCESS_TOKEN=<Super Admin access token> npm run seed:api
 *
 * A Super Admin token is needed: only a Super Admin may set a company's cooldown. Tokens live
 * 15 minutes, which is enough. The one-time device activation codes and agent PINs are written to
 * seed-secrets.local.json next to this script (git-ignored), never printed.
 *
 * Refuses to run twice: it stops if the main demo company already exists.
 */
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { createSeedDataset } from '../src/mocks/seed';

const API = (process.env.ZAWED_API_URL ?? 'http://localhost:8080').replace(/\/$/, '');
const TOKEN = process.env.ZAWED_ACCESS_TOKEN;
/** Stay under the API's 300 requests per minute per user. */
const PAUSE_MS = 220;

const FUEL: Record<string, string> = { gas92: 'GAS_92', gas95: 'GAS_95', diesel: 'DIESEL' };
const COMPANY_STATUS: Record<string, string> = { frozen: 'FROZEN' };
const VEHICLE_STATUS: Record<string, string> = { suspended: 'SUSPENDED' };

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

async function api<T>(method: string, path: string, body?: unknown): Promise<T> {
  for (let attempt = 0; attempt < 5; attempt++) {
    await sleep(PAUSE_MS);
    const response = await fetch(`${API}/api/v1${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${TOKEN}`,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (response.status === 429) {
      const wait = Number(response.headers.get('Retry-After') ?? '5');
      await sleep(wait * 1000);
      continue;
    }
    const text = await response.text();
    if (!response.ok) {
      throw new Error(`${method} ${path} -> ${response.status} ${text}`);
    }
    return (text ? JSON.parse(text) : undefined) as T;
  }
  throw new Error(`${method} ${path}: still rate limited after 5 attempts`);
}

/** A tag UID that looks like a real 7-byte ISO 14443 UID. */
function uid(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(6));
  return '04' + Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('').toUpperCase();
}

async function main() {
  if (!TOKEN) {
    throw new Error('Set ZAWED_ACCESS_TOKEN to a Super Admin access token.');
  }
  const data = createSeedDataset();
  const nile = data.companies['co-nile'];
  if (!nile) throw new Error('The demo dataset has no main company');

  const existing = await api<{ items: unknown[] }>('GET', `/ops/companies?q=${encodeURIComponent(nile.name.en)}`);
  if (existing.items.length > 0) {
    throw new Error(`"${nile.name.en}" already exists: this API is already seeded.`);
  }

  const ids = { company: new Map<string, string>(), department: new Map<string, string>(), vehicle: new Map<string, string>(), station: new Map<string, string>() };
  const secrets: { devices: Record<string, string>; agents: Record<string, { workerId: string; pin: string }> } = {
    devices: {},
    agents: {},
  };

  // Companies, their policy, their departments.
  for (const company of Object.values(data.companies)) {
    const created = await api<{ id: string }>('POST', '/ops/companies', {
      company: { legalName: company.name.en, nameAr: company.name.ar, nameEn: company.name.en },
      cooldownMinutes: company.cooldownMinutes,
      lowBalanceThresholdPiastres: company.lowBalanceThresholdPiastres,
    });
    ids.company.set(company.id, created.id);
    const status = COMPANY_STATUS[company.status];
    if (status) {
      await api('POST', `/ops/companies/${created.id}/status`, { status, reason: 'Demo dataset' });
    }
  }
  for (const department of Object.values(data.departments)) {
    const created = await api<{ id: string }>('POST', `/ops/companies/${ids.company.get(department.companyId)}/departments`, {
      name: department.name.en,
      monthlyBudgetPiastres: department.monthlyBudgetPiastres,
    });
    ids.department.set(department.id, created.id);
  }
  console.log(`companies: ${ids.company.size}, departments: ${ids.department.size}`);

  // Vehicles, one tag each, and their status.
  for (const vehicle of Object.values(data.vehicles)) {
    const created = await api<{ id: string }>('POST', '/ops/vehicles', {
      companyId: ids.company.get(vehicle.companyId),
      vehicle: {
        departmentId: ids.department.get(vehicle.departmentId),
        plate: vehicle.plate,
        make: vehicle.make.en,
        model: vehicle.model.en,
        year: vehicle.year,
        fuelType: FUEL[vehicle.fuelTypeId],
        tankCapacityMl: vehicle.tankCapacityMl,
        tier: vehicle.tier === 'vip' ? 'VIP' : 'NORMAL',
      },
    });
    ids.vehicle.set(vehicle.id, created.id);
    await api('POST', `/ops/vehicles/${created.id}/tags`, { uid: uid() });
    const status = VEHICLE_STATUS[vehicle.status];
    if (status) {
      await api('POST', `/ops/vehicles/${created.id}/status`, { status, reason: 'Demo dataset' });
    }
  }
  console.log(`vehicles and tags: ${ids.vehicle.size}`);

  // Limits: the demo's rules list vehicles; the API keeps one rule per vehicle, kind and period.
  let limits = 0;
  for (const rule of data.limitRules) {
    for (const vehicleId of rule.vehicleIds) {
      const id = ids.vehicle.get(vehicleId);
      if (!id) continue;
      await api('PUT', `/ops/vehicles/${id}/limits`, {
        kind: rule.kind === 'volume' ? 'VOLUME' : 'AMOUNT',
        period: rule.period.toUpperCase(),
        maxValue: rule.maxValue,
      });
      limits++;
    }
  }
  console.log(`limits: ${limits}`);

  // The station operator, its stations, devices and agents.
  const operators = new Map<string, string>();
  for (const operator of Object.values(data.operators)) {
    const created = await api<{ id: string }>('POST', '/ops/operators', {
      legalName: operator.name.en,
      nameAr: operator.name.ar,
      nameEn: operator.name.en,
    });
    operators.set(operator.id, created.id);
  }
  for (const station of Object.values(data.stations)) {
    const created = await api<{ id: string }>('POST', '/ops/stations', {
      operatorId: operators.get(station.operatorId),
      station: {
        nameAr: station.name.ar,
        nameEn: station.name.en,
        timeZone: station.timeZone,
        connectivity: station.connectivity === 'low' ? 'LOW' : 'NORMAL',
        fuelTypes: Object.keys(station.prices).map((fuel) => FUEL[fuel]),
      },
    });
    ids.station.set(station.id, created.id);
  }
  for (const device of Object.values(data.devices)) {
    const created = await api<{ device: { code: string }; activationCode: string }>('POST', '/ops/devices', {
      stationId: ids.station.get(device.stationId),
      label: device.code,
    });
    secrets.devices[`${device.code} (${created.device.code})`] = created.activationCode;
  }
  for (const agent of Object.values(data.agents)) {
    const created = await api<{ agent: { workerId: string }; pin: string }>('POST', '/ops/agents', {
      stationId: ids.station.get(agent.stationId),
      fullName: agent.name.en,
    });
    secrets.agents[agent.name.en] = { workerId: created.agent.workerId, pin: created.pin };
  }
  console.log(`operators: ${operators.size}, stations: ${ids.station.size}, devices: ${Object.keys(secrets.devices).length}, agents: ${Object.keys(secrets.agents).length}`);

  // National prices: every demo station charges the same, which the API models as one national price.
  const anyStation = Object.values(data.stations)[0];
  for (const [fuel, price] of Object.entries(anyStation?.prices ?? {})) {
    await api('POST', '/ops/prices', { fuelType: FUEL[fuel], pricePiastres: price, note: 'Demo dataset' });
  }
  console.log('prices: scheduled');

  const out = join(dirname(fileURLToPath(import.meta.url)), 'seed-secrets.local.json');
  writeFileSync(out, JSON.stringify(secrets, null, 2));
  console.log(`one-time device codes and agent PINs written to ${out}`);
}

main().catch((error: unknown) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});
