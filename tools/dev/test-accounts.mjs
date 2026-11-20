// Creates one test account per role on zawed-dev and funds the demo company's wallet, through the API.
// Needs MFA switched off on zawed-dev (ZAWED_MFA_REQUIRED=false) and the seed data (npm run seed:api).
// Env: ZAWED_API_URL, ADMIN_EMAIL, ADMIN_PASSWORD, TEST_PASSWORD, OUT (file path for the account list).
import { randomUUID } from 'node:crypto';
import { writeFileSync } from 'node:fs';

const API = process.env.ZAWED_API_URL.replace(/\/$/, '') + '/api/v1';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function call(method, path, token, body, extraHeaders = {}) {
  await sleep(250);
  const res = await fetch(API + path, {
    method,
    headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(body ? { 'Content-Type': 'application/json' } : {}), ...extraHeaders },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  const json = text ? JSON.parse(text) : null;
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${json?.code ?? ''} ${json?.message ?? ''}`);
  return json;
}

async function login(door, email, password) {
  const r = await call('POST', door === 'admin' ? '/admin/auth/login' : '/auth/login', null, { email, password });
  if (r.status !== 'AUTHENTICATED') throw new Error(`login ${email}: ${r.status} (is ZAWED_MFA_REQUIRED=false deployed?)`);
  return r.session;
}

const admin = await login('admin', process.env.ADMIN_EMAIL, process.env.ADMIN_PASSWORD);
const A = admin.accessToken;

const companies = await call('GET', '/ops/companies?size=50', A);
const company = companies.items.find((c) => /nile/i.test(JSON.stringify(c.name ?? c))) ?? companies.items[0];
const operators = await call('GET', '/ops/operators?size=50', A);
const operator = operators.items[0];
if (!company || !operator) throw new Error('No company or operator found: run the seed first');

const TEST = process.env.TEST_PASSWORD;
const people = [
  { key: 'ops', role: 'OPS', scope: { type: 'PLATFORM' }, name: 'Test Ops' },
  { key: 'opsManager', role: 'OPS_MANAGER', scope: { type: 'PLATFORM' }, name: 'Test Ops Manager' },
  { key: 'fleetManager', role: 'FLEET_MANAGER', scope: { type: 'COMPANY', id: company.id }, name: 'Test Fleet Manager' },
  { key: 'accountant', role: 'ACCOUNTANT', scope: { type: 'COMPANY', id: company.id }, name: 'Test Accountant' },
  { key: 'stationManager', role: 'STATION_MANAGER', scope: { type: 'OPERATOR', id: operator.id }, name: 'Test Station Manager' },
];
const existing = await call('GET', '/admin/users?size=100', A);
const sessions = {};
for (const p of people) {
  const email = `test.${p.key.replace(/[A-Z]/g, (m) => '.' + m.toLowerCase())}@zawed.test`;
  p.email = email;
  if (existing.items.some((u) => u.email === email)) {
    sessions[p.key] = await login('user', email, TEST);
    continue;
  }
  const created = await call('POST', '/admin/users', A, { email, fullName: p.name, role: p.role, scope: p.scope });
  const s = await login('user', email, created.temporaryPassword);
  await call('POST', '/auth/password', s.accessToken, { currentPassword: created.temporaryPassword, newPassword: TEST });
  sessions[p.key] = await login('user', email, TEST);
}

// Fund the company's wallet: Ops records a verified bank transfer, a different person (Ops Manager) approves.
const ops = sessions.ops.accessToken;
const topup = await call('POST', `/ops/companies/${company.id}/wallet/credits`, ops, {
  amountPiastres: 50_000_000, bankReference: `TEST-${Date.now()}`, transferDate: new Date().toISOString().slice(0, 10),
  evidenceReference: 'Test funding for the owner', note: 'Test data',
}, { 'Idempotency-Key': randomUUID() });
await call('POST', `/ops/topups/${topup.id}/approve`, sessions.opsManager.accessToken, {}, { 'Idempotency-Key': randomUUID() });
const wallet = await call('GET', `/ops/companies/${company.id}/wallet`, ops);

const lines = [
  '# zawed-dev test accounts',
  '',
  `Web: https://zawed-web-production.up.railway.app — staff sign in at /login, the Super Admin at /admin/login.`,
  `All test accounts share one password: ${TEST}`,
  '',
  '| Role | Email | Scope |',
  '|---|---|---|',
  `| Super Admin | ${process.env.ADMIN_EMAIL} | platform (your own account, own password) |`,
  ...people.map((p) => `| ${p.role} | ${p.email} | ${p.scope.type === 'PLATFORM' ? 'platform' : p.scope.type === 'COMPANY' ? 'company: ' + (company.name?.en ?? company.id) : 'operator: ' + (operator.name?.en ?? operator.id)} |`),
  '',
  `Wallet of ${company.name?.en ?? company.id}: ${JSON.stringify(wallet.balancePiastres ?? wallet)} piastres after a 500,000 EGP test top-up.`,
  'POS device activation codes and agent PINs: see seed-secrets for zawed-dev (same folder).',
];
writeFileSync(process.env.OUT, lines.join('\n') + '\n');
console.log('done:', people.map((p) => p.role).join(', '), '| wallet', wallet.balancePiastres);
