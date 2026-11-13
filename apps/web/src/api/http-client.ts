/**
 * The one place the web app talks HTTP to the ZAWED API. Every failure, whatever its cause, leaves
 * here as an ApiRequestError with a stable `code`, so screens choose their wording from the code
 * and never from the server's English message.
 */
import type { components } from './generated/schema';

/** The error envelope every API refusal has (generated from the API's OpenAPI description). */
export type ApiErrorBody = components['schemas']['ApiError'];

/** Same origin by default: the dev server proxies /api to the local API, production serves both. */
export const API_BASE_URL: string = (import.meta.env.VITE_API_BASE_URL ?? '/api/v1').replace(/\/+$/, '');

/** No response arrived: offline, DNS, CORS, or the server is down. */
export const NETWORK_ERROR = 'NETWORK_ERROR';

/** A response arrived but was not the API's JSON. */
export const INVALID_RESPONSE = 'INVALID_RESPONSE';

interface ApiRequestErrorInit {
  status: number;
  code: string;
  message: string;
  retryable: boolean;
  requestId?: string;
  field?: string;
  fields?: ApiErrorBody['fields'];
  params?: ApiErrorBody['params'];
}

export class ApiRequestError extends Error {
  /** HTTP status, or 0 when no response arrived. */
  readonly status: number;
  /** Stable, machine-readable reason. UI copy is keyed on this. */
  readonly code: string;
  /** Whether sending the same request again can succeed. */
  readonly retryable: boolean;
  /** Matches the server log line; show it to the user so support can find the failure. */
  readonly requestId?: string;
  readonly field?: string;
  readonly fields?: ApiErrorBody['fields'];
  readonly params?: ApiErrorBody['params'];

  constructor(init: ApiRequestErrorInit) {
    super(init.message);
    this.name = 'ApiRequestError';
    this.status = init.status;
    this.code = init.code;
    this.retryable = init.retryable;
    this.requestId = init.requestId;
    this.field = init.field;
    this.fields = init.fields;
    this.params = init.params;
  }
}

function isErrorBody(value: unknown): value is ApiErrorBody {
  return typeof value === 'object' && value !== null && typeof (value as { code?: unknown }).code === 'string';
}

async function toError(response: Response, requestId: string | undefined): Promise<ApiRequestError> {
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    body = undefined; // not JSON: a proxy or gateway answered instead of the API
  }
  if (isErrorBody(body)) {
    return new ApiRequestError({
      status: response.status,
      code: body.code,
      message: body.message,
      retryable: body.retryable,
      requestId: body.requestId ?? requestId,
      field: body.field,
      fields: body.fields,
      params: body.params,
    });
  }
  return new ApiRequestError({
    status: response.status,
    code: `HTTP_${response.status}`,
    message: response.statusText || 'Request failed',
    retryable: response.status === 429 || response.status >= 500,
    requestId,
  });
}

/**
 * How requests are authenticated. The auth module registers this once; the client itself knows
 * nothing about sessions, so it has no import cycle with them.
 */
export interface AuthHooks {
  /** The current access token, if signed in. Held in memory only. */
  getAccessToken: () => string | undefined;
  /** Gets a new access token after a 401. Resolves false when the session is over. Must be single-flight. */
  refresh: () => Promise<boolean>;
}

let authHooks: AuthHooks | undefined;

export function configureAuth(hooks: AuthHooks | undefined): void {
  authHooks = hooks;
}

export interface ApiRequestInit extends RequestInit {
  /** Do not send the access token, and do not refresh on 401 (sign-in, refresh, logout). */
  anonymous?: boolean;
  /** Serialized as the JSON body. */
  json?: unknown;
}

/**
 * Sends a request to the API and returns the parsed JSON body.
 * `path` is relative to the API base, e.g. `/meta`.
 *
 * When signed in, the access token is attached. If the API answers 401 because the token expired,
 * the session is refreshed once and the request is sent again.
 */
export async function apiRequest<T>(path: string, init: ApiRequestInit = {}): Promise<T> {
  try {
    return await send<T>(path, init);
  } catch (error) {
    const canRetry =
      !init.anonymous && authHooks && error instanceof ApiRequestError && error.status === 401 && error.code === 'UNAUTHENTICATED';
    if (!canRetry || !(await authHooks!.refresh())) throw error;
    return send<T>(path, init);
  }
}

async function send<T>(path: string, init: ApiRequestInit): Promise<T> {
  const { anonymous, json, ...requestInit } = init;
  const headers = new Headers(requestInit.headers);
  if (!headers.has('Accept')) headers.set('Accept', 'application/json');
  if (json !== undefined) headers.set('Content-Type', 'application/json');
  const token = anonymous ? undefined : authHooks?.getAccessToken();
  if (token) headers.set('Authorization', `Bearer ${token}`);

  let response: Response;
  try {
    response = await fetch(`${API_BASE_URL}${path}`, {
      ...requestInit,
      headers,
      body: json !== undefined ? JSON.stringify(json) : requestInit.body,
      // The refresh cookie is same-origin only (SameSite=Strict, path /api/v1/auth).
      credentials: 'same-origin',
    });
  } catch (cause) {
    // A request the caller cancelled is not a failure to report.
    if (init.signal?.aborted) throw cause;
    throw new ApiRequestError({ status: 0, code: NETWORK_ERROR, message: 'The API could not be reached', retryable: true });
  }

  const requestId = response.headers.get('X-Request-Id') ?? undefined;
  if (!response.ok) throw await toError(response, requestId);
  if (response.status === 204) return undefined as T;
  try {
    return (await response.json()) as T;
  } catch {
    // A success that is not JSON: something other than the API answered (e.g. the web server's index.html).
    throw new ApiRequestError({
      status: response.status,
      code: INVALID_RESPONSE,
      message: 'The response was not valid JSON',
      retryable: false,
      requestId,
    });
  }
}
