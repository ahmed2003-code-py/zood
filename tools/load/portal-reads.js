// Ops and company portal reads with date ranges: fueling lists (newest first, with and without
// from/to), the activity calendar and the CSV export. These are the read paths measured in
// QueryPlanReport (performance report §3); run this while online-fueling.js writes, to see reads
// and writes together.
//
// NOT EXECUTED (k6 was not installed where this was written). See README.md.
//
//   k6 run -e BASE_URL=https://staging.example -e FIXTURE=./fixture.json \
//          -e OPS_EMAIL=... -e OPS_PASSWORD=... -e OPS_TOTP=... portal-reads.js
//
// Rate limit: one signed-in user may make 300 calls a minute. The Ops scenario uses one Ops user,
// so it stays at 4 requests a second; the company scenario spreads over the seeded Fleet Managers.

import exec from 'k6/execution';
import { check } from 'k6';
import { authed, loadFixture, signIn, tokenSource } from './lib/zawed.js';

const fixture = loadFixture();

export const options = {
  scenarios: {
    ops: {
      executor: 'constant-arrival-rate',
      rate: Number(__ENV.OPS_RATE || 4),
      timeUnit: '1s',
      duration: __ENV.HOLD || '10m',
      preAllocatedVUs: 4,
      maxVUs: 8,
      exec: 'opsReads',
    },
    company: {
      executor: 'constant-arrival-rate',
      rate: Math.max(1, Math.min(Number(__ENV.COMPANY_RATE || 10), fixture.portalUsers.length * 4)),
      timeUnit: '1s',
      duration: __ENV.HOLD || '10m',
      preAllocatedVUs: Math.max(1, fixture.portalUsers.length),
      maxVUs: Math.max(1, fixture.portalUsers.length),
      exec: 'companyReads',
    },
  },
  thresholds: {
    // PROPOSED acceptance targets for reads.
    'http_req_duration{kind:list}': ['p(95)<500'],
    'http_req_duration{kind:activity}': ['p(95)<1000'],
    'http_req_duration{kind:export}': ['p(95)<3000'],
    http_req_failed: ['rate<0.001'],
  },
};

const day = 24 * 60 * 60 * 1000;
const iso = (ms) => new Date(ms).toISOString();
const date = (ms) => new Date(ms).toISOString().slice(0, 10);

/** One month, `back` months before now (calendar months in UTC; the API reads Cairo days). */
function month(back) {
  const now = new Date();
  const from = Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - back, 1);
  const to = Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - back + 1, 1);
  return { from, to };
}

let opsToken;

export function opsReads() {
  opsToken = opsToken || tokenSource(__ENV.OPS_EMAIL, __ENV.OPS_PASSWORD, __ENV.OPS_TOTP);
  const token = opsToken();
  const company = fixture.companies[exec.scenario.iterationInTest % fixture.companies.length].id;
  const station = fixture.stations[exec.scenario.iterationInTest % fixture.stations.length];
  const last = month(1);
  const old = month(6);
  const requests = [
    ['/ops/fuelings', 'list'],
    [`/ops/fuelings?from=${iso(Date.now() - 30 * day)}&to=${iso(Date.now())}`, 'list'],
    [`/ops/fuelings?from=${iso(old.from)}&to=${iso(old.to)}`, 'list'],
    [`/ops/fuelings?companyId=${company}&from=${iso(old.from)}&to=${iso(old.to)}`, 'list'],
    [`/ops/fuelings?stationId=${station.id}&from=${iso(old.from)}&to=${iso(old.to)}`, 'list'],
    [`/ops/activity?from=${date(old.from)}&to=${date(old.to - day)}`, 'activity'],
    [`/ops/activity?companyId=${company}&from=${date(old.from)}&to=${date(old.to - day)}`, 'activity'],
    [`/ops/fuelings/export?companyId=${company}&from=${iso(last.from)}&to=${iso(last.to)}`, 'export'],
    [`/ops/fuelings/export?operatorId=${station.operatorId}&from=${iso(last.from)}&to=${iso(last.to)}`, 'export'],
  ];
  const [path, kind] = requests[exec.scenario.iterationInTest % requests.length];
  const r = authed('GET', path, token, undefined, path.split('?')[0], { kind });
  check(r, { [`ops ${kind} 200`]: (x) => x.status === 200 }, { kind });
}

const companyTokens = {};

export function companyReads() {
  if (fixture.portalUsers.length === 0) return;
  const user = fixture.portalUsers[(exec.vu.idInTest - 1) % fixture.portalUsers.length];
  if (!companyTokens[user.email] || Date.now() - companyTokens[user.email].at > 12 * 60 * 1000) {
    companyTokens[user.email] = { token: signIn(user.email, user.password, null), at: Date.now() };
  }
  const token = companyTokens[user.email].token;
  const last = month(1);
  const old = month(6);
  const requests = [
    ['/company/fuelings', 'list'],
    [`/company/fuelings?from=${iso(Date.now() - 30 * day)}&to=${iso(Date.now())}`, 'list'],
    [`/company/fuelings?from=${iso(old.from)}&to=${iso(old.to)}`, 'list'],
    [`/company/activity?from=${date(old.from)}&to=${date(old.to - day)}`, 'activity'],
    [`/company/fuelings/export?from=${iso(last.from)}&to=${iso(last.to)}`, 'export'],
  ];
  const [path, kind] = requests[exec.scenario.iterationInTest % requests.length];
  const r = authed('GET', path, token, undefined, path.split('?')[0], { kind });
  check(r, { [`company ${kind} 200`]: (x) => x.status === 200 }, { kind });
}
