import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiRequest, ApiRequestError, INVALID_RESPONSE, NETWORK_ERROR } from './http-client';

function respondWith(body: unknown, init: ResponseInit) {
  const fetchMock = vi.fn(async () => new Response(typeof body === 'string' ? body : JSON.stringify(body), init));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

async function failureOf(promise: Promise<unknown>): Promise<ApiRequestError> {
  const error = await promise.then(
    () => undefined,
    (reason: unknown) => reason,
  );
  expect(error).toBeInstanceOf(ApiRequestError);
  return error as ApiRequestError;
}

afterEach(() => vi.unstubAllGlobals());

describe('apiRequest', () => {
  it('returns the parsed body and asks for JSON on the API base path', async () => {
    const fetchMock = respondWith({ service: 'zawed-api' }, { status: 200 });

    await expect(apiRequest('/meta')).resolves.toEqual({ service: 'zawed-api' });

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/v1/meta');
    expect(new Headers(init.headers).get('Accept')).toBe('application/json');
  });

  it('turns the API error envelope into an ApiRequestError keyed by code', async () => {
    respondWith(
      { statusCode: 401, code: 'UNAUTHENTICATED', message: 'Authentication is required', retryable: false, requestId: 'req-1' },
      { status: 401, headers: { 'X-Request-Id': 'req-1' } },
    );

    const error = await failureOf(apiRequest('/companies'));

    expect(error.status).toBe(401);
    expect(error.code).toBe('UNAUTHENTICATED');
    expect(error.retryable).toBe(false);
    expect(error.requestId).toBe('req-1');
  });

  it('keeps field issues from a validation failure', async () => {
    respondWith(
      {
        statusCode: 400,
        code: 'VALIDATION_FAILED',
        message: 'The request is not valid',
        retryable: false,
        fields: [{ field: 'volumeMl', code: 'POSITIVE' }],
      },
      { status: 400 },
    );

    const error = await failureOf(apiRequest('/pos/fuelings', { method: 'POST' }));

    expect(error.fields).toEqual([{ field: 'volumeMl', code: 'POSITIVE' }]);
  });

  it('still gives a code and the request id when the answer is not the API envelope', async () => {
    respondWith('<html>Bad gateway</html>', { status: 502, headers: { 'X-Request-Id': 'edge-9' } });

    const error = await failureOf(apiRequest('/meta'));

    expect(error.code).toBe('HTTP_502');
    expect(error.retryable).toBe(true);
    expect(error.requestId).toBe('edge-9');
  });

  it('reports a success that is not JSON as an invalid response, not a crash', async () => {
    respondWith('<!doctype html><html></html>', { status: 200, headers: { 'X-Request-Id': 'web-1' } });

    const error = await failureOf(apiRequest('/meta'));

    expect(error.code).toBe(INVALID_RESPONSE);
    expect(error.retryable).toBe(false);
    expect(error.requestId).toBe('web-1');
  });

  it('reports an unreachable API as a retryable network error', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('fetch failed');
      }),
    );

    const error = await failureOf(apiRequest('/meta'));

    expect(error.status).toBe(0);
    expect(error.code).toBe(NETWORK_ERROR);
    expect(error.retryable).toBe(true);
  });

  it('lets a cancelled request reject with the abort, not as an API failure', async () => {
    const controller = new AbortController();
    controller.abort();
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new DOMException('Aborted', 'AbortError');
      }),
    );

    await expect(apiRequest('/meta', { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' });
  });
});
