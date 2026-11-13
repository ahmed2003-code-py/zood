import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Session } from './types';

const session = (token: string): Session => ({
  accessToken: token,
  expiresInSeconds: 900,
  user: {
    id: 'u1',
    email: 'ops@zawed.example',
    fullName: 'Sara Mansour',
    role: 'OPS',
    scope: { type: 'PLATFORM' },
    mustChangePassword: false,
    mfaEnabled: true,
  },
});

const refusal = () =>
  new Response(JSON.stringify({ statusCode: 401, code: 'SESSION_EXPIRED', message: 'expired', retryable: false }), { status: 401 });

beforeEach(() => {
  vi.resetModules();
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

async function loadStore() {
  return (await import('./session-store')).useSession;
}

describe('session store', () => {
  it('sends one refresh for many concurrent callers', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(session('fresh')), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    const useSession = await loadStore();

    const results = await Promise.all([1, 2, 3, 4, 5].map(() => useSession.getState().refresh()));

    expect(results).toEqual([true, true, true, true, true]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(useSession.getState().accessToken).toBe('fresh');
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/v1/auth/refresh');
    expect(new Headers(init.headers).get('X-Zawed-Client')).toBe('web');
  });

  it('ends the session when the server refuses the refresh', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => refusal()));
    const useSession = await loadStore();
    useSession.getState().establish('user', session('old'));

    await expect(useSession.getState().refresh()).resolves.toBe(false);

    expect(useSession.getState().status).toBe('anonymous');
    expect(useSession.getState().accessToken).toBeUndefined();
  });

  it('keeps the session through a network failure', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('offline');
      }),
    );
    const useSession = await loadStore();
    useSession.getState().establish('user', session('kept'));

    await expect(useSession.getState().refresh()).resolves.toBe(false);

    expect(useSession.getState().status).toBe('authenticated');
    expect(useSession.getState().accessToken).toBe('kept');
  });

  it('refreshes before the access token expires', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(session('next')), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    const useSession = await loadStore();
    useSession.getState().establish('user', session('first'));

    await vi.advanceTimersByTimeAsync((900 - 60) * 1000);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(useSession.getState().accessToken).toBe('next');
  });

  it('uses the admin door for a Super Admin session', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(session('admin')), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    const useSession = await loadStore();
    useSession.getState().establish('admin', session('first'));

    await useSession.getState().refresh();

    expect((fetchMock.mock.calls[0] as unknown as [string])[0]).toBe('/api/v1/admin/auth/refresh');
  });

  it('keeps the token out of browser storage', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(session('secret-token')), { status: 200 })));
    const written: string[] = [];
    vi.stubGlobal('localStorage', {
      getItem: () => null,
      setItem: (_key: string, value: string) => written.push(value),
    });
    vi.stubGlobal('window', { localStorage: globalThis.localStorage });
    const useSession = await loadStore();

    await useSession.getState().refresh();

    expect(written.join(' ')).not.toContain('secret-token');
  });
});
