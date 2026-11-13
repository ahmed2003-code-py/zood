/**
 * The signed-in session, held in memory only.
 *
 * - The access token never touches localStorage or sessionStorage: a script injected into the page
 *   could read those. The refresh token is an HttpOnly cookie the page cannot read at all.
 * - After a reload the page has no token, so it asks /auth/refresh for one (the cookie travels by
 *   itself). A readable hint cookie says whether there is a session worth asking about.
 * - Refreshing is single-flight: ten requests failing with 401 at once cause one refresh.
 * - Signing out in one tab signs out every tab.
 */
import { create } from 'zustand';
import { ApiRequestError, configureAuth } from '@/api/http-client';
import { readStorage, writeStorage } from '@/lib/storage';
import { authApi } from './auth-api';
import type { Door, Session, SessionUser } from './types';

export type SessionStatus = 'unknown' | 'anonymous' | 'authenticated';

interface SessionState {
  status: SessionStatus;
  door: Door;
  user?: SessionUser;
  accessToken?: string;
  /** Applies a session returned by sign-in or refresh. */
  establish: (door: Door, session: Session) => void;
  /** Updates the user after a profile change (e.g. the password was changed). */
  setUser: (user: SessionUser) => void;
  /** Called once on load: restores the session from the refresh cookie if there is one. */
  restore: () => Promise<void>;
  /** Gets a new access token. Resolves false when the session is over. */
  refresh: () => Promise<boolean>;
  signOut: () => Promise<void>;
}

const DOOR_KEY = 'zawed.auth.door';
const SESSION_HINT_COOKIE = 'zawed_session';
/** Refresh this long before the access token expires, so a request rarely meets a 401. */
const REFRESH_MARGIN_SECONDS = 60;

let inflight: Promise<boolean> | undefined;
let refreshTimer: ReturnType<typeof setTimeout> | undefined;
const channel = typeof BroadcastChannel !== 'undefined' ? new BroadcastChannel('zawed.auth') : undefined;

function hasSessionHint(): boolean {
  return typeof document !== 'undefined' && document.cookie.split(';').some((part) => part.trim().startsWith(`${SESSION_HINT_COOKIE}=`));
}

function storedDoor(): Door {
  return readStorage(DOOR_KEY) === 'admin' ? 'admin' : 'user';
}

function scheduleRefresh(expiresInSeconds: number, run: () => void) {
  clearTimeout(refreshTimer);
  const delay = Math.max(10, expiresInSeconds - REFRESH_MARGIN_SECONDS) * 1000;
  refreshTimer = setTimeout(run, delay);
}

export const useSession = create<SessionState>((set, get) => ({
  status: 'unknown',
  door: storedDoor(),

  establish: (door, session) => {
    writeStorage(DOOR_KEY, door);
    set({ status: 'authenticated', door, user: session.user, accessToken: session.accessToken });
    scheduleRefresh(session.expiresInSeconds, () => void get().refresh());
  },

  setUser: (user) => set({ user }),

  restore: async () => {
    if (get().status !== 'unknown') return;
    if (!hasSessionHint()) {
      set({ status: 'anonymous' });
      return;
    }
    await get().refresh();
  },

  refresh: () => {
    inflight ??= (async () => {
      const door = get().door;
      try {
        get().establish(door, await authApi.refresh(door));
        return true;
      } catch (error) {
        // Only a refusal ends the session. A network failure keeps what we have and lets the caller fail.
        if (error instanceof ApiRequestError && (error.status === 401 || error.status === 403)) {
          clearTimeout(refreshTimer);
          set({ status: 'anonymous', user: undefined, accessToken: undefined });
        } else if (get().status === 'unknown') {
          set({ status: 'anonymous' });
        }
        return false;
      } finally {
        inflight = undefined;
      }
    })();
    return inflight;
  },

  signOut: async () => {
    const door = get().door;
    clearTimeout(refreshTimer);
    set({ status: 'anonymous', user: undefined, accessToken: undefined });
    channel?.postMessage('signed-out');
    try {
      await authApi.logout(door);
    } catch {
      // The local session is gone either way; the server session expires on its own.
    }
  },
}));

channel?.addEventListener('message', (event) => {
  if (event.data === 'signed-out') {
    clearTimeout(refreshTimer);
    useSession.setState({ status: 'anonymous', user: undefined, accessToken: undefined });
  }
});

configureAuth({
  getAccessToken: () => useSession.getState().accessToken,
  refresh: () => useSession.getState().refresh(),
});
