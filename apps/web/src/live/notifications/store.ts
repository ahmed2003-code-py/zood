/**
 * The notification state every header shares: the unread count (polled politely) and the feed
 * that the bell opens. Framework-free so it can be tested; `use-notifications.ts` binds it to React.
 *
 * Marking read is optimistic: it changes no money, and a failure puts the row and the count back.
 */
import type { Notification, Page, PageQuery, UnreadNotifications } from '../api';
import { createPoller, type Poller } from './model';

export interface NotificationsApi {
  list: (q: PageQuery & { unread?: boolean }) => Promise<Page<Notification>>;
  unreadCount: () => Promise<UnreadNotifications>;
  markRead: (id: string) => Promise<void>;
  markAllRead: () => Promise<void>;
}

export interface NotificationsState {
  count: number;
  capped: boolean;
  items: Notification[];
  nextCursor?: string;
  /** The feed's first page is on its way. */
  loading: boolean;
  loadingMore: boolean;
  error: boolean;
  /** The feed has been loaded at least once since the store started. */
  loaded: boolean;
}

export const FEED_PAGE = 20;
export const POLL_MS = 60_000;

const INITIAL: NotificationsState = { count: 0, capped: false, items: [], loading: false, loadingMore: false, error: false, loaded: false };

export interface NotificationsStore {
  getState: () => NotificationsState;
  subscribe: (listener: () => void) => () => void;
  /** Starts polling for the first subscriber and stops after the last one leaves. */
  retain: () => () => void;
  /** Loads the feed's first page (when the bell opens). */
  openFeed: () => Promise<void>;
  loadMore: () => Promise<void>;
  markRead: (id: string) => Promise<void>;
  markAllRead: () => Promise<void>;
}

export function createNotificationsStore(
  api: NotificationsApi,
  makePoller: (run: () => Promise<unknown>) => Poller = (run) => createPoller({ run, intervalMs: POLL_MS }),
): NotificationsStore {
  let state = INITIAL;
  const listeners = new Set<() => void>();
  let retainers = 0;
  let poller: Poller | undefined;

  const set = (patch: Partial<NotificationsState>) => {
    state = { ...state, ...patch };
    for (const listener of listeners) listener();
  };

  const fetchCount = async () => {
    const result = await api.unreadCount();
    set({ count: result.count, capped: result.capped });
  };

  return {
    getState: () => state,
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    retain: () => {
      retainers += 1;
      if (!poller) {
        poller = makePoller(fetchCount);
        poller.start();
      }
      return () => {
        retainers -= 1;
        if (retainers === 0) {
          poller?.stop();
          poller = undefined;
        }
      };
    },
    openFeed: async () => {
      set({ loading: true, error: false });
      try {
        const page = await api.list({ limit: FEED_PAGE });
        set({ items: page.items, nextCursor: page.nextCursor ?? undefined, loading: false, loaded: true });
        // The count may have moved since the last poll; ask again so the badge matches the list.
        poller?.refresh();
      } catch {
        set({ loading: false, error: true });
      }
    },
    loadMore: async () => {
      const cursor = state.nextCursor;
      if (!cursor || state.loadingMore) return;
      set({ loadingMore: true });
      try {
        const page = await api.list({ limit: FEED_PAGE, cursor });
        set({ items: [...state.items, ...page.items], nextCursor: page.nextCursor ?? undefined, loadingMore: false });
      } catch {
        set({ loadingMore: false, error: true });
      }
    },
    markRead: async (id) => {
      const target = state.items.find((item) => item.id === id);
      if (target?.read) return;
      const before = { items: state.items, count: state.count };
      set({
        items: state.items.map((item) => (item.id === id ? { ...item, read: true, readAt: new Date().toISOString() } : item)),
        count: state.capped ? state.count : Math.max(0, state.count - 1),
      });
      try {
        await api.markRead(id);
      } catch {
        set(before);
      }
    },
    markAllRead: async () => {
      const before = { items: state.items, count: state.count, capped: state.capped };
      const now = new Date().toISOString();
      set({ items: state.items.map((item) => (item.read ? item : { ...item, read: true, readAt: now })), count: 0, capped: false });
      try {
        await api.markAllRead();
        poller?.refresh();
      } catch {
        set(before);
      }
    },
  };
}
