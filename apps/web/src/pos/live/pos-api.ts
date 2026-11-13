/**
 * The POS terminal's side of the device channel (B4 contract §1): activation, short-lived device
 * tokens signed by the device key, the agent session, and fuelings. A POS never holds a user's
 * token; every call here goes out anonymous and carries the device's own credentials.
 */
import type { components } from '@/api/generated/schema';
import { ApiRequestError, apiRequest } from '@/api/http-client';
import { createKeyPair, exportPublicKey, forgetKeyPair, loadKeyPair, sign } from './device-key';

type S = components['schemas'];
export type PosFuelingRequest = S['PosFuelingRequest'];
export type PosFueling = S['PosFuelingResponse'];
export type PosAgent = S['PosAgentResponse'];
export type DeclineReason = NonNullable<PosFueling['declineReason']>;
export type WalletStatus = NonNullable<PosFueling['walletStatus']>;

/** What this browser remembers about the device it has become. Not a secret: the key is. */
export interface DeviceIdentity {
  deviceId: string;
  deviceCode: string;
  stationId: string;
}

const DEVICE_KEY = 'zawed.pos.device';
const SESSION_KEY = 'zawed.pos.agent';
/** Ask for a new device token this long before the current one expires. */
const TOKEN_MARGIN_MS = 60_000;

export function loadDevice(): DeviceIdentity | undefined {
  try {
    const raw = localStorage.getItem(DEVICE_KEY);
    return raw ? (JSON.parse(raw) as DeviceIdentity) : undefined;
  } catch {
    return undefined;
  }
}

function saveDevice(device: DeviceIdentity | undefined): void {
  try {
    if (device) localStorage.setItem(DEVICE_KEY, JSON.stringify(device));
    else localStorage.removeItem(DEVICE_KEY);
  } catch {
    // Storage blocked: the device must be activated again next time.
  }
}

/** The agent's session, per tab, like a shift on one terminal. */
export interface AgentSession {
  sessionToken: string;
  expiresAt: string;
  agent: PosAgent;
}

export function loadSession(): AgentSession | undefined {
  try {
    const raw = sessionStorage.getItem(SESSION_KEY);
    const session = raw ? (JSON.parse(raw) as AgentSession) : undefined;
    return session && new Date(session.expiresAt).getTime() > Date.now() ? session : undefined;
  } catch {
    return undefined;
  }
}

export function saveSession(session: AgentSession | undefined): void {
  try {
    if (session) sessionStorage.setItem(SESSION_KEY, JSON.stringify(session));
    else sessionStorage.removeItem(SESSION_KEY);
  } catch {
    // Storage blocked: the agent signs in again on reload.
  }
}

// ---------------------------------------------------------------------------------- activation

export async function activate(deviceCode: string, activationCode: string): Promise<DeviceIdentity> {
  const pair = await createKeyPair();
  try {
    const answer = await apiRequest<S['ActivateResponse']>('/pos/activate', {
      method: 'POST',
      anonymous: true,
      json: { deviceCode: deviceCode.trim(), activationCode: activationCode.trim(), publicKey: await exportPublicKey(pair) },
    });
    const device = { deviceId: answer.deviceId, deviceCode: answer.deviceCode, stationId: answer.stationId };
    saveDevice(device);
    return device;
  } catch (error) {
    await forgetKeyPair();
    throw error;
  }
}

/** Forget this device here: its key, its identity and any agent session. Ops must issue a new code. */
export async function deactivateHere(): Promise<void> {
  token = undefined;
  saveSession(undefined);
  saveDevice(undefined);
  await forgetKeyPair();
}

// -------------------------------------------------------------------------------- device token

let token: { value: string; expiresAt: number } | undefined;
let pending: Promise<string> | undefined;

async function deviceToken(device: DeviceIdentity): Promise<string> {
  if (token && token.expiresAt - TOKEN_MARGIN_MS > Date.now()) return token.value;
  // Single flight: parallel calls share one signed request (each nonce may be used once).
  pending ??= (async () => {
    try {
      const pair = await loadKeyPair();
      if (!pair) throw new ApiRequestError({ status: 401, code: 'DEVICE_NOT_ACTIVATED', message: 'No device key', retryable: false });
      const timestamp = Math.floor(Date.now() / 1000);
      const nonce = crypto.randomUUID();
      const signature = await sign(pair, `zawed-device-token\n${device.deviceId}\n${timestamp}\n${nonce}`);
      const issued = await apiRequest<S['DeviceTokenResponse']>('/pos/token', {
        method: 'POST',
        anonymous: true,
        json: { deviceId: device.deviceId, timestamp, nonce, signature },
      });
      token = { value: issued.token, expiresAt: new Date(issued.expiresAt).getTime() };
      return issued.token;
    } finally {
      pending = undefined;
    }
  })();
  return pending;
}

/** A call as the device (and the agent, when a session is given). A stale token is renewed once. */
async function posRequest<T>(device: DeviceIdentity, path: string, init: { method: string; json?: unknown; session?: string }): Promise<T> {
  const send = async () =>
    apiRequest<T>(path, {
      method: init.method,
      anonymous: true,
      json: init.json,
      headers: {
        Authorization: `Bearer ${await deviceToken(device)}`,
        ...(init.session ? { 'X-Agent-Session': init.session } : {}),
      },
    });
  try {
    return await send();
  } catch (error) {
    if (!(error instanceof ApiRequestError) || error.code !== 'DEVICE_UNAUTHENTICATED' || !token) throw error;
    token = undefined;
    return send();
  }
}

// ------------------------------------------------------------------------------------- agents

export async function agentSignIn(device: DeviceIdentity, workerId: string, pin: string): Promise<AgentSession> {
  const answer = await posRequest<S['AgentLoginResponse']>(device, '/pos/agents/login', {
    method: 'POST',
    json: { workerId: workerId.trim(), pin },
  });
  const session = { sessionToken: answer.sessionToken, expiresAt: answer.expiresAt, agent: answer.agent };
  saveSession(session);
  return session;
}

export async function changePin(device: DeviceIdentity, session: AgentSession, currentPin: string, newPin: string): Promise<AgentSession> {
  await posRequest<void>(device, '/pos/agents/pin', {
    method: 'POST',
    session: session.sessionToken,
    json: { currentPin, newPin },
  });
  const next = { ...session, agent: { ...session.agent, mustChangePin: false } };
  saveSession(next);
  return next;
}

export async function agentSignOut(device: DeviceIdentity, session: AgentSession): Promise<void> {
  try {
    await posRequest<void>(device, '/pos/agents/logout', { method: 'POST', session: session.sessionToken });
  } finally {
    saveSession(undefined);
  }
}

// ----------------------------------------------------------------------------------- fuelings

export function recordFueling(device: DeviceIdentity, session: AgentSession, request: PosFuelingRequest): Promise<PosFueling> {
  return posRequest<PosFueling>(device, '/pos/fuelings', { method: 'POST', session: session.sessionToken, json: request });
}

/** Errors after which the agent must sign in again rather than retry. */
export const SESSION_ENDED = new Set(['AGENT_SESSION_INVALID', 'AGENT_SESSION_REQUIRED']);
/** Errors after which this browser is no longer a working device. */
export const DEVICE_ENDED = new Set(['DEVICE_UNAUTHENTICATED', 'DEVICE_NOT_ACTIVATED']);
