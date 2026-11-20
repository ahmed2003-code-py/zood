// Seeds a staging environment for the load scripts, through the public API only.
//
// NOT EXECUTED (k6 was not installed where this was written). See README.md.
//
//   k6 run -e BASE_URL=https://staging.example -e COMPANIES=10 -e VEHICLES_PER_COMPANY=300 \
//          -e OPS_USERS=./ops-users.json -e ADMIN_EMAIL=... -e ADMIN_PASSWORD=... -e ADMIN_TOTP=... \
//          -e RUN=1 seed.js
//
// setup() builds the network and the companies with the first Ops user and writes everything the
// load scripts need to fixture.json (handleSummary). The vehicles are then created in parallel, one
// VU per Ops user (an Ops user may make 300 calls a minute). Plates and tag UIDs are computed from
// (RUN, company, vehicle), so the fixture does not list them; use a new RUN for every seeding,
// because a tag UID is bound once, ever.
//
// The fixture holds the PKCS#8 private keys of the synthetic devices: use it against staging only.

import exec from 'k6/execution';
import { sleep } from 'k6';
import {
  AGENT_PIN,
  Device,
  activate,
  authed,
  expectStatus,
  exportKeys,
  newKeyPair,
  plate,
  signIn,
  tagUid,
  tokenSource,
} from './lib/zawed.js';

const OPS_USERS = JSON.parse(open(__ENV.OPS_USERS || './ops-users.json')); // [{ email, password, totpSecret }]
const RUN = Number(__ENV.RUN || 1);
const COMPANIES = Number(__ENV.COMPANIES || 10);
const VEHICLES_PER_COMPANY = Number(__ENV.VEHICLES_PER_COMPANY || 300);
const OPERATORS = Number(__ENV.OPERATORS || 5);
const STATIONS_PER_OPERATOR = Number(__ENV.STATIONS_PER_OPERATOR || 4);
const DEVICES_PER_STATION = Number(__ENV.DEVICES_PER_STATION || 3);
const PORTAL_USERS = Number(__ENV.PORTAL_USERS || Math.min(COMPANIES, 10));
const CREDIT_PIASTRES = Number(__ENV.CREDIT_PIASTRES || 100_000_000); // 1,000,000 EGP per company
const PRICE_PIASTRES = Number(__ENV.PRICE_PIASTRES || 1_500);
const PORTAL_PASSWORD = __ENV.PORTAL_PASSWORD || 'load-test portal password 2026';

export const options = {
  setupTimeout: '120m',
  scenarios: {
    vehicles: { executor: 'per-vu-iterations', vus: OPS_USERS.length, iterations: 1, maxDuration: '240m' },
  },
};

// Calls by one Ops user stay under 300 a minute.
const PACE_SECONDS = 0.21;

export async function setup() {
  const ops = tokenSource(OPS_USERS[0].email, OPS_USERS[0].password, OPS_USERS[0].totpSecret);
  const call = (method, path, body, status, what) => {
    sleep(PACE_SECONDS);
    return expectStatus(authed(method, path, ops(), body, what), status, what).json();
  };

  // Operators, stations, prices.
  const stations = [];
  for (let o = 0; o < OPERATORS; o++) {
    const operator = call('POST', '/ops/operators', { legalName: `Load Operator ${RUN}-${o}`, nameAr: `مشغل ${o}`, nameEn: `Load Operator ${o}` }, 201, 'ops/operators');
    for (let s = 0; s < STATIONS_PER_OPERATOR; s++) {
      const station = call(
        'POST',
        '/ops/stations',
        {
          operatorId: operator.id,
          station: { nameAr: `محطة ${o}-${s}`, nameEn: `Load Station ${o}-${s}`, connectivity: 'NORMAL', fuelTypes: ['GAS_92', 'DIESEL'] },
        },
        201,
        'ops/stations',
      );
      for (const fuelType of ['GAS_92', 'DIESEL']) {
        call('POST', '/ops/prices', { fuelType, stationId: station.id, pricePiastres: PRICE_PIASTRES }, 201, 'ops/prices');
      }
      stations.push({ id: station.id, operatorId: operator.id });
    }
  }

  // Devices: registered, activated with a fresh P-256 key, an agent of the station signed in once
  // and its PIN changed (a new agent must). Activation is limited to 10 a minute per address.
  const devices = [];
  for (const station of stations) {
    for (let d = 0; d < DEVICES_PER_STATION; d++) {
      const registered = call('POST', '/ops/devices', { stationId: station.id }, 201, 'ops/devices');
      const pair = await newKeyPair();
      const keys = await exportKeys(pair);
      sleep(6.5);
      expectStatus(activate(registered.device.code, registered.activationCode, keys.publicKey), 200, 'pos/activate');
      const agent = call('POST', '/ops/agents', { stationId: station.id, fullName: `Load Agent ${RUN}-${devices.length}` }, 201, 'ops/agents');
      const device = await new Device({ id: registered.device.id, privateKey: keys.privateKey }).init();
      await device.agentLogin(agent.agent.workerId, agent.pin);
      await device.changePin(agent.pin, AGENT_PIN);
      devices.push({
        id: registered.device.id,
        stationId: station.id,
        operatorId: station.operatorId,
        privateKey: keys.privateKey,
        workerId: agent.agent.workerId,
        agentId: agent.agent.id,
      });
    }
  }

  // Companies with money in the wallet.
  const companies = [];
  for (let c = 0; c < COMPANIES; c++) {
    const company = call(
      'POST',
      '/ops/companies',
      { company: { legalName: `Load Company ${RUN}-${c}`, nameAr: `شركة ${c}`, nameEn: `Load Company ${c}` }, lowBalanceThresholdPiastres: 0 },
      201,
      'ops/companies',
    );
    call(
      'POST',
      `/ops/companies/${company.id}/wallet/credits`,
      { amountPiastres: CREDIT_PIASTRES, bankReference: `LOAD-${RUN}-${c}-${Date.now()}`, transferDate: new Date().toISOString().slice(0, 10) },
      201,
      'ops/wallet/credits',
    );
    companies.push({ id: company.id });
  }

  // Portal users (Fleet Manager, no second factor) for the first companies, if a Super Admin is given.
  const portalUsers = [];
  if (__ENV.ADMIN_EMAIL) {
    const admin = signIn(__ENV.ADMIN_EMAIL, __ENV.ADMIN_PASSWORD, __ENV.ADMIN_TOTP);
    for (let c = 0; c < PORTAL_USERS; c++) {
      sleep(PACE_SECONDS);
      const email = `load-${RUN}-${c}@zawed.test`;
      const created = expectStatus(
        authed('POST', '/admin/users', admin, { email, fullName: `Load Fleet Manager ${c}`, role: 'FLEET_MANAGER', scope: { type: 'COMPANY', id: companies[c].id } }, 'admin/users'),
        201,
        'admin/users',
      ).json();
      const temporary = signIn(email, created.temporaryPassword, null);
      expectStatus(authed('POST', '/auth/password', temporary, { currentPassword: created.temporaryPassword, newPassword: PORTAL_PASSWORD }, 'auth/password'), 204, 'auth/password');
      portalUsers.push({ email, password: PORTAL_PASSWORD, companyId: companies[c].id });
    }
  }

  return { run: RUN, vehiclesPerCompany: VEHICLES_PER_COMPANY, companies, stations, devices, portalUsers, seededAt: new Date().toISOString() };
}

/** One VU per Ops user creates its share of the vehicles, each with its deterministic tag. */
export default function (data) {
  const me = exec.vu.idInTest - 1;
  const user = OPS_USERS[me];
  const ops = tokenSource(user.email, user.password, user.totpSecret);
  const total = data.companies.length * data.vehiclesPerCompany;
  for (let i = me; i < total; i += OPS_USERS.length) {
    const companyIndex = i % data.companies.length;
    const vehicleIndex = Math.floor(i / data.companies.length);
    sleep(PACE_SECONDS);
    const vehicle = expectStatus(
      authed(
        'POST',
        '/ops/vehicles',
        ops(),
        {
          companyId: data.companies[companyIndex].id,
          vehicle: { plate: plate(data.run, companyIndex, vehicleIndex), make: 'Toyota', model: 'Hilux', fuelType: 'GAS_92', tankCapacityMl: 60000, tier: 'NORMAL' },
        },
        'ops/vehicles',
      ),
      201,
      'ops/vehicles',
    ).json();
    sleep(PACE_SECONDS);
    expectStatus(authed('POST', `/ops/vehicles/${vehicle.id}/tags`, ops(), { uid: tagUid(data.run, companyIndex, vehicleIndex) }, 'ops/vehicles/tags'), 201, 'ops/vehicles/tags');
  }
}

export function handleSummary(data) {
  return { [__ENV.FIXTURE || 'fixture.json']: JSON.stringify(data.setup_data, null, 2) };
}
