import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiRequest, ApiRequestError, configureAuth } from './http-client';

const unauthenticated = () =>
  new Response(JSON.stringify({ statusCode: 401, code: 'UNAUTHENTICATED', message: 'expired', retryable: false }), { status: 401 });
const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });

afterEach(() => {
  configureAuth(undefined);
  vi.unstubAllGlobals();
});

describe('apiRequest with a session', () => {
  it('sends the access token and the same-origin credentials mode', async () => {
    const fetchMock = vi.fn(async () => ok({ id: 'u1' }));
    vi.stubGlobal('fetch', fetchMock);
    configureAuth({ getAccessToken: () => 'token-1', refresh: async () => true });

    await apiRequest('/auth/me');

    const init = (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1];
    expect(new Headers(init.headers).get('Authorization')).toBe('Bearer token-1');
    expect(init.credentials).toBe('same-origin');
  });

  it('refreshes once after an expired token and sends the request again with the new token', async () => {
    let token = 'old';
    const fetchMock = vi.fn().mockResolvedValueOnce(unauthenticated()).mockResolvedValueOnce(ok({ id: 'u1' }));
    vi.stubGlobal('fetch', fetchMock);
    const refresh = vi.fn(async () => {
      token = 'new';
      return true;
    });
    configureAuth({ getAccessToken: () => token, refresh });

    await expect(apiRequest('/auth/me')).resolves.toEqual({ id: 'u1' });

    expect(refresh).toHaveBeenCalledTimes(1);
    const retried = (fetchMock.mock.calls[1] as unknown as [string, RequestInit])[1];
    expect(new Headers(retried.headers).get('Authorization')).toBe('Bearer new');
  });

  it('gives up with the original error when the session cannot be refreshed', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => unauthenticated()));
    configureAuth({ getAccessToken: () => 'old', refresh: async () => false });

    const error = await apiRequest('/auth/me').catch((reason: unknown) => reason);

    expect(error).toBeInstanceOf(ApiRequestError);
    expect((error as ApiRequestError).code).toBe('UNAUTHENTICATED');
  });

  it('never refreshes or sends a token for sign-in requests', async () => {
    const fetchMock = vi.fn(async () => unauthenticated());
    vi.stubGlobal('fetch', fetchMock);
    const refresh = vi.fn(async () => true);
    configureAuth({ getAccessToken: () => 'token', refresh });

    await apiRequest('/auth/login', { method: 'POST', anonymous: true, json: { email: 'a@b.c', password: 'x' } }).catch(() => undefined);

    expect(refresh).not.toHaveBeenCalled();
    const init = (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1];
    expect(new Headers(init.headers).get('Authorization')).toBeNull();
    expect(new Headers(init.headers).get('Content-Type')).toBe('application/json');
    expect(init.body).toBe(JSON.stringify({ email: 'a@b.c', password: 'x' }));
  });

  it('does not refresh on refusals that are not about an expired token', async () => {
    const forbidden = new Response(JSON.stringify({ statusCode: 403, code: 'FORBIDDEN', message: 'no', retryable: false }), {
      status: 403,
    });
    vi.stubGlobal('fetch', vi.fn(async () => forbidden));
    const refresh = vi.fn(async () => true);
    configureAuth({ getAccessToken: () => 'token', refresh });

    await apiRequest('/admin/users').catch(() => undefined);

    expect(refresh).not.toHaveBeenCalled();
  });
});
