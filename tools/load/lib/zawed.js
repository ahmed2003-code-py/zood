// Shared helpers for the ZAWED k6 scripts: configuration, sign-in (with TOTP), the POS device
// channel (P-256 key, signed token requests, agent sessions) and the deterministic synthetic fleet.
//
// NOT EXECUTED: k6 was not installed on the machine these scripts were written on. They follow the
// API as documented in docs/architecture/b4-fueling-contract.md and b5-sync-contract.md and as used
// by apps/web/src/pos/live/pos-api.ts, and need k6 v1.x (global WebCrypto `crypto.subtle`).

import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { b64decode, b64encode } from 'k6/encoding';
import { hmac } from 'k6/crypto';

export const BASE_URL = (__ENV.BASE_URL || 'http://localhost:8080').replace(/\/$/, '');
export const API = `${BASE_URL}/api/v1`;
/** The POS protocol version these scripts speak (B5 contract §1). */
export const POS_CONTRACT = '1';
/** The PIN every seeded agent is given after the forced first change. */
export const AGENT_PIN = __ENV.AGENT_PIN || '482915';

const JSON_HEADERS = { 'Content-Type': 'application/json' };

// ---------------------------------------------------------------------------------- fixture

/** The file seed.js wrote (companies, stations, devices with their keys, agents, portal users). */
export function loadFixture() {
  const path = __ENV.FIXTURE || './fixture.json';
  return JSON.parse(open(path));
}

/**
 * The synthetic fleet is deterministic: vehicle v of company c has plate and tag UID computed from
 * the indices and the seed run, so load scripts need no list of tags. A UID is 7 bytes (14 hex
 * characters): 4-byte UIDs are left out of the POS snapshot.
 */
export function tagUid(run, companyIndex, vehicleIndex) {
  return `7A${hex(run, 2)}${hex(companyIndex, 4)}${hex(vehicleIndex, 6)}`;
}

export function plate(run, companyIndex, vehicleIndex) {
  return `LT ${run}-${companyIndex}-${vehicleIndex}`;
}

function hex(n, width) {
  return n.toString(16).toUpperCase().padStart(width, '0');
}

/** The car for a global, never repeated index: spreads consecutive fuelings over all companies. */
export function carFor(fixture, index) {
  const companies = fixture.companies.length;
  const companyIndex = index % companies;
  const vehicleIndex = Math.floor(index / companies) % fixture.vehiclesPerCompany;
  return {
    companyIndex,
    vehicleIndex,
    companyId: fixture.companies[companyIndex].id,
    uid: tagUid(fixture.run, companyIndex, vehicleIndex),
  };
}

// ------------------------------------------------------------------------------- sign-in

/** RFC 6238 TOTP (SHA-1, 30 s, 6 digits) for a base32 secret, at a step offset from now. */
export function totp(secretBase32, stepOffset = 0) {
  const key = base32Decode(secretBase32);
  const step = Math.floor(Date.now() / 1000 / 30) + stepOffset;
  const counter = new Uint8Array(8);
  let value = step;
  for (let i = 7; i >= 0; i--) {
    counter[i] = value & 0xff;
    value = Math.floor(value / 256);
  }
  const mac = new Uint8Array(hmac('sha1', key.buffer, counter.buffer, 'binary'));
  const offset = mac[mac.length - 1] & 0x0f;
  const code =
    (((mac[offset] & 0x7f) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3]) % 1000000;
  return String(code).padStart(6, '0');
}

function base32Decode(text) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const clean = text.replace(/=+$/, '').replace(/\s/g, '').toUpperCase();
  const out = [];
  let bits = 0;
  let value = 0;
  for (const ch of clean) {
    const idx = alphabet.indexOf(ch);
    if (idx < 0) fail(`not base32: ${ch}`);
    value = (value << 5) | idx;
    bits += 5;
    if (bits >= 8) {
      out.push((value >>> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return Uint8Array.from(out);
}

/**
 * Signs in and answers the TOTP step when the role needs one. Every role, Super Admin included,
 * signs in at /auth/login (ADR 0008 amendment). Returns the access token (valid 15 minutes).
 */
export function signIn(email, password, totpSecret) {
  const login = http.post(`${API}/auth/login`, JSON.stringify({ email, password }), { headers: JSON_HEADERS, tags: { name: 'auth/login' } });
  if (login.status !== 200) fail(`sign-in of ${email} failed: ${login.status} ${login.body}`);
  const body = login.json();
  if (body.status === 'AUTHENTICATED') return body.session.accessToken;
  if (body.status !== 'MFA_REQUIRED' || !totpSecret) fail(`sign-in of ${email}: ${body.status} (a TOTP secret is needed)`);
  // A code is accepted once per step: try the current step, then the next one.
  for (let offset = 0; offset <= 1; offset++) {
    const verified = http.post(`${API}/auth/mfa/verify`, JSON.stringify({ challengeToken: body.challengeToken, code: totp(totpSecret, offset) }), {
      headers: JSON_HEADERS,
      tags: { name: 'auth/mfa' },
    });
    if (verified.status === 200) return verified.json().session.accessToken;
  }
  fail(`MFA for ${email} failed`);
}

/** An access token that renews itself before its 15 minutes run out. */
export function tokenSource(email, password, totpSecret) {
  let token;
  let issuedAt = 0;
  return () => {
    if (!token || Date.now() - issuedAt > 12 * 60 * 1000) {
      if (token) sleep(31); // a TOTP code is used once per 30 s step
      token = signIn(email, password, totpSecret);
      issuedAt = Date.now();
    }
    return token;
  };
}

export function authed(method, path, token, body, name, tags = {}) {
  const params = { headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : JSON_HEADERS) }, tags: { name: name || path, ...tags } };
  return http.request(method, `${API}${path}`, body === undefined ? null : JSON.stringify(body), params);
}

export function expectStatus(response, status, what) {
  if (!check(response, { [`${what} ${status}`]: (r) => r.status === status })) {
    fail(`${what}: expected ${status}, got ${response.status} ${response.body}`);
  }
  return response;
}

// ---------------------------------------------------------------------------- POS device

/** WebCrypto signs ECDSA as r‖s; the API verifies DER with Java's SHA256withECDSA (as device-key.ts). */
export function rawToDer(raw) {
  if (raw.length !== 64) fail('An ECDSA P-256 signature is 64 bytes');
  const integer = (bytes) => {
    let start = 0;
    while (start < bytes.length - 1 && bytes[start] === 0) start++;
    const trimmed = Array.from(bytes.subarray(start));
    return [0x02, ...(trimmed[0] & 0x80 ? [trimmed.length + 1, 0x00] : [trimmed.length]), ...trimmed];
  };
  const r = integer(raw.subarray(0, 32));
  const s = integer(raw.subarray(32));
  return Uint8Array.from([0x30, r.length + s.length, ...r, ...s]);
}

const ECDSA = { name: 'ECDSA', namedCurve: 'P-256' };

export async function newKeyPair() {
  return crypto.subtle.generateKey(ECDSA, true, ['sign', 'verify']);
}

export async function exportKeys(pair) {
  const spki = new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey));
  const pkcs8 = new Uint8Array(await crypto.subtle.exportKey('pkcs8', pair.privateKey));
  return { publicKey: b64encode(spki.buffer), privateKey: b64encode(pkcs8.buffer) };
}

export async function importPrivateKey(pkcs8Base64) {
  return crypto.subtle.importKey('pkcs8', b64decode(pkcs8Base64), ECDSA, false, ['sign']);
}

export async function sign(privateKey, message) {
  const raw = new Uint8Array(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, privateKey, stringToBytes(message)));
  return b64encode(rawToDer(raw).buffer);
}

function stringToBytes(text) {
  const bytes = [];
  for (let i = 0; i < text.length; i++) bytes.push(text.charCodeAt(i)); // the message is ASCII
  return Uint8Array.from(bytes).buffer;
}

function uuid() {
  return crypto.randomUUID();
}
export { uuid };

/** POST /pos/activate: binds the device's public key with the one-time code Ops issued. */
export function activate(deviceCode, activationCode, publicKey) {
  return http.post(`${API}/pos/activate`, JSON.stringify({ deviceCode, activationCode, publicKey }), {
    headers: { ...JSON_HEADERS, 'X-Zawed-Contract': POS_CONTRACT },
    tags: { name: 'pos/activate' },
  });
}

/**
 * One POS terminal: its key, a device token renewed before its 15 minutes run out (each request
 * signs a fresh nonce), and the agent session.
 */
export class Device {
  constructor(device) {
    this.id = device.id;
    this.privateKeyBase64 = device.privateKey;
    this.token = null;
    this.tokenExpiresAt = 0;
    this.session = null;
  }

  async init() {
    this.key = await importPrivateKey(this.privateKeyBase64);
    return this;
  }

  async deviceToken() {
    if (this.token && this.tokenExpiresAt - 60000 > Date.now()) return this.token;
    const timestamp = Math.floor(Date.now() / 1000);
    const nonce = uuid();
    const signature = await sign(this.key, `zawed-device-token\n${this.id}\n${timestamp}\n${nonce}`);
    const issued = http.post(`${API}/pos/token`, JSON.stringify({ deviceId: this.id, timestamp, nonce, signature }), {
      headers: { ...JSON_HEADERS, 'X-Zawed-Contract': POS_CONTRACT },
      tags: { name: 'pos/token' },
    });
    expectStatus(issued, 200, 'device token');
    this.token = issued.json().token;
    this.tokenExpiresAt = Date.parse(issued.json().expiresAt);
    return this.token;
  }

  async request(method, path, body, withSession, name) {
    const headers = { Authorization: `Bearer ${await this.deviceToken()}`, 'X-Zawed-Contract': POS_CONTRACT };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (withSession && this.session) headers['X-Agent-Session'] = this.session;
    return http.request(method, `${API}${path}`, body === undefined ? null : JSON.stringify(body), { headers, tags: { name } });
  }

  async agentLogin(workerId, pin) {
    const login = await this.request('POST', '/pos/agents/login', { workerId, pin }, false, 'pos/agents/login');
    expectStatus(login, 200, 'agent sign-in');
    this.session = login.json().sessionToken;
    this.agentId = login.json().agent.id;
    return login.json();
  }

  async changePin(currentPin, newPin) {
    const changed = await this.request('POST', '/pos/agents/pin', { currentPin, newPin }, true, 'pos/agents/pin');
    expectStatus(changed, 204, 'PIN change');
  }

  async fuel(uid, volumeMl, clientTxnId = uuid()) {
    return this.request(
      'POST',
      '/pos/fuelings',
      { clientTxnId, tagUid: uid, volumeMl, deviceAt: new Date().toISOString(), receiptLocale: 'ar' },
      true,
      'pos/fuelings',
    );
  }

  async sync(items, pending = 0) {
    return this.request('POST', '/pos/sync', { batchId: uuid(), pending, items }, false, 'pos/sync');
  }
}
