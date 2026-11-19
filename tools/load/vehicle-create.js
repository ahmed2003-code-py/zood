// Vehicle creation by Operations: POST /ops/vehicles, then POST /ops/vehicles/{id}/tags.
//
// NOT EXECUTED (k6 was not installed where this was written). See README.md.
//
//   k6 run -e BASE_URL=https://staging.example -e FIXTURE=./fixture.json -e OPS_USERS=./ops-users.json \
//          -e RUN=2 vehicle-create.js
//
// One Ops user may make 300 calls a minute, that is 2.5 vehicles with their tag a second; each VU
// is one Ops user from OPS_USERS, paced at 2 vehicles a second. Use a RUN not used by seed.js or an
// earlier run: plates and tag UIDs are derived from it, and a tag UID is bound once, ever.

import exec from 'k6/execution';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';
import { authed, loadFixture, tokenSource } from './lib/zawed.js';

const fixture = loadFixture();
const OPS_USERS = JSON.parse(open(__ENV.OPS_USERS || './ops-users.json'));
const RUN = Number(__ENV.RUN || 2);

export const options = {
  scenarios: {
    create: {
      executor: 'constant-arrival-rate',
      rate: 2 * OPS_USERS.length,
      timeUnit: '1s',
      duration: __ENV.HOLD || '5m',
      preAllocatedVUs: OPS_USERS.length,
      maxVUs: OPS_USERS.length,
    },
  },
  thresholds: {
    // PROPOSED: creating a vehicle and issuing its tag each within 500 ms at p95.
    'http_req_duration{name:ops/vehicles}': ['p(95)<500'],
    'http_req_duration{name:ops/vehicles/tags}': ['p(95)<500'],
    http_req_failed: ['rate<0.001'],
  },
};

const vehicleMs = new Trend('vehicle_create_ms', true);
const tagMs = new Trend('tag_issue_ms', true);
const tokens = {};

export default function () {
  const me = (exec.vu.idInTest - 1) % OPS_USERS.length;
  const user = OPS_USERS[me];
  tokens[me] = tokens[me] || tokenSource(user.email, user.password, user.totpSecret, 'user');
  const n = exec.scenario.iterationInTest;
  const company = fixture.companies[n % fixture.companies.length].id;
  const vehicle = authed(
    'POST',
    '/ops/vehicles',
    tokens[me](),
    {
      companyId: company,
      vehicle: { plate: `LV ${RUN}-${n}`, make: 'Toyota', model: 'Hilux', fuelType: 'DIESEL', tankCapacityMl: 80000, tier: 'NORMAL' },
    },
    'ops/vehicles',
  );
  vehicleMs.add(vehicle.timings.duration);
  if (!check(vehicle, { 'vehicle 201': (r) => r.status === 201 })) return;
  const uid = `7B${RUN.toString(16).toUpperCase().padStart(2, '0')}${n.toString(16).toUpperCase().padStart(10, '0')}`;
  const tag = authed('POST', `/ops/vehicles/${vehicle.json('id')}/tags`, tokens[me](), { uid }, 'ops/vehicles/tags');
  tagMs.add(tag.timings.duration);
  check(tag, { 'tag 201': (r) => r.status === 201 });
  sleep(0.1);
}
