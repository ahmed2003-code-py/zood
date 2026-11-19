// Online fueling through the real POS protocol: device token signed with the device's P-256 key,
// agent sign-in, then POST /pos/fuelings at a constant arrival rate.
//
// NOT EXECUTED (k6 was not installed where this was written). See README.md.
//
//   k6 run -e BASE_URL=https://staging.example -e FIXTURE=./fixture.json -e RATE=50 online-fueling.js
//
// Every VU is one terminal (one seeded device and its agent). A device may make 120 POS requests a
// minute, so RATE must stay below devices × 2 per second; 1 per second per device is the safe plan.
// Every fueling uses a car never used before in this run (a tag fuels once per cooldown, 120 to 240
// minutes): the fleet must hold at least RATE × duration tags, or later fuelings are declined with
// COOLDOWN, which is a cheaper path than a completed one and makes the result unrepresentative.

import exec from 'k6/execution';
import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import { AGENT_PIN, Device, carFor, loadFixture } from './lib/zawed.js';

const fixture = loadFixture();
const RATE = Number(__ENV.RATE || 50);
const CAR_OFFSET = Number(__ENV.CAR_OFFSET || 0); // skip cars an earlier run in the same cooldown used
const VOLUME_ML = Number(__ENV.VOLUME_ML || 20000);

export const options = {
  scenarios: {
    online: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: fixture.devices.length,
      maxVUs: fixture.devices.length,
      stages: [
        { target: Math.ceil(RATE / 5), duration: '1m' }, // warm-up
        { target: RATE, duration: '2m' }, // ramp to the target
        { target: RATE, duration: __ENV.HOLD || '10m' }, // hold
        { target: Math.ceil(RATE * 1.5), duration: '2m' }, // 150 % burst
        { target: 0, duration: '1m' },
      ],
    },
  },
  thresholds: {
    // PROPOSED acceptance targets (docs/artifacts/production-hardening/performance-report.md).
    'http_req_duration{name:pos/fuelings}': ['p(95)<300'],
    'http_req_failed{name:pos/fuelings}': ['rate<0.001'],
    fueling_completed: ['rate>0.999'],
  },
};

const completed = new Rate('fueling_completed');
const declined = new Counter('fueling_declined');
const latency = new Trend('fueling_latency', true);

let device;

async function terminal() {
  if (!device) {
    // VUs are not more than devices: VU n drives device n.
    const seeded = fixture.devices[(exec.vu.idInTest - 1) % fixture.devices.length];
    device = await new Device(seeded).init();
    await device.agentLogin(seeded.workerId, AGENT_PIN);
  }
  return device;
}

export default async function () {
  const pos = await terminal();
  const car = carFor(fixture, CAR_OFFSET + exec.scenario.iterationInTest);
  const response = await pos.fuel(car.uid, VOLUME_ML);
  latency.add(response.timings.duration);
  const ok = check(response, {
    'fueling 201': (r) => r.status === 201,
    'fueling COMPLETED': (r) => r.status === 201 && r.json('status') === 'COMPLETED',
  });
  completed.add(ok);
  if (response.status === 201 && response.json('status') === 'DECLINED') {
    declined.add(1, { reason: response.json('declineReason') });
  }
}
