// Offline sync: terminals that were offline send their queued captures in batches (POST /pos/sync).
//
// NOT EXECUTED (k6 was not installed where this was written). See README.md.
//
//   k6 run -e BASE_URL=https://staging.example -e FIXTURE=./fixture.json -e BATCH=50 offline-sync.js
//
// Each VU is one terminal: it signs its agent in (so a session covers the capture times and the
// captures are not flagged AGENT_UNVERIFIED), then repeatedly sends a batch of BATCH captures dated
// a few minutes back. A device may send 30 batches a minute. The captures use cars from CAR_OFFSET
// on, so they do not collide with the online scenario. Every batch is then sent a second time,
// which must answer DUPLICATE for every item (idempotent retry after a lost answer).
//
// One device may record 10,000 EGP offline per company per 24 hours (zawed.sync.offline-exposure-
// cap-piastres); past it a capture is still recorded but flagged OVER_OFFLINE_ALLOWANCE and its
// payable held. With 20 L at 15 EGP that is 33 captures per device and company: spread the captures
// over many companies, as carFor does, or the run measures the held path.

import exec from 'k6/execution';
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { AGENT_PIN, Device, carFor, loadFixture, uuid } from './lib/zawed.js';

const fixture = loadFixture();
const BATCH = Math.min(100, Number(__ENV.BATCH || 50));
const CAR_OFFSET = Number(__ENV.CAR_OFFSET || 1_000_000);
const VOLUME_ML = Number(__ENV.VOLUME_ML || 20000);

export const options = {
  scenarios: {
    sync: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { target: Math.min(fixture.devices.length, Number(__ENV.VUS || 20)), duration: '2m' },
        { target: Math.min(fixture.devices.length, Number(__ENV.VUS || 20)), duration: __ENV.HOLD || '5m' },
        { target: 0, duration: '1m' },
      ],
    },
  },
  thresholds: {
    // PROPOSED: a batch of 50 within 5 s at p95; no RETRY or REJECTED results.
    'http_req_duration{name:pos/sync}': ['p(95)<5000'],
    'http_req_failed{name:pos/sync}': ['rate<0.001'],
    sync_item_problem: ['count==0'],
  },
};

const problems = new Counter('sync_item_problem');
const perItem = new Trend('sync_ms_per_item', true);

let device;

export default async function () {
  if (!device) {
    const seeded = fixture.devices[(exec.vu.idInTest - 1) % fixture.devices.length];
    device = await new Device(seeded).init();
    await device.agentLogin(seeded.workerId, AGENT_PIN);
  }
  const base = CAR_OFFSET + exec.scenario.iterationInTest * BATCH;
  const now = Date.now();
  const items = [];
  for (let i = 0; i < BATCH; i++) {
    const car = carFor(fixture, base + i);
    items.push({
      clientTxnId: uuid(),
      tagUid: car.uid,
      volumeMl: VOLUME_ML,
      deviceAt: new Date(now - (BATCH - i) * 2000).toISOString(),
      agentId: device.agentId,
      receiptLocale: 'ar',
    });
  }
  const first = await device.sync(items, 0);
  check(first, { 'sync 200': (r) => r.status === 200 });
  if (first.status === 200) {
    perItem.add(first.timings.duration / BATCH);
    for (const result of first.json('results')) {
      if (result.result !== 'RECORDED') problems.add(1, { result: result.result, code: result.code || '' });
    }
  }
  const again = await device.sync(items, 0);
  check(again, {
    'resend 200': (r) => r.status === 200,
    'resend all DUPLICATE': (r) => r.status === 200 && r.json('results').every((x) => x.result === 'DUPLICATE'),
  });
}
