import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Notification } from '../api';
import { codeParam, createPoller, intParam, notificationLink, unreadBadge } from './model';
import { createNotificationsStore, type NotificationsApi } from './store';

const note = (overrides: Partial<Notification> = {}): Notification => ({
  id: 'n1',
  type: 'TOPUP_AWAITING_APPROVAL',
  entityType: 'TOPUP',
  entityId: 't1',
  params: { amountPiastres: 4_000_000, companyId: 'c1' },
  createdAt: '2026-11-21T08:00:00Z',
  read: false,
  ...overrides,
});

describe('notification links', () => {
  it('opens the record in the portal that shows it', () => {
    expect(notificationLink(note(), 'ops')).toBe('/ops/topups?status=PENDING_APPROVAL');
    expect(notificationLink(note({ entityType: 'WALLET_ADJUSTMENT' }), 'ops')).toBe('/ops/topups?tab=adjustments');
    expect(notificationLink(note({ entityType: 'ALERT', entityId: 'a9' }), 'ops')).toBe('/ops/alerts/a9');
    expect(notificationLink(note({ entityType: 'TICKET', entityId: 'k1' }), 'company')).toBe('/company/tickets/k1');
    expect(notificationLink(note({ entityType: 'TOPUP' }), 'company')).toBe('/company/wallet');
    expect(notificationLink(note({ entityType: 'COMPANY_WALLET' }), 'company')).toBe('/company/wallet');
    expect(notificationLink(note({ entityType: 'PAYOUT' }), 'station')).toBe('/station/settlements');
    expect(notificationLink(note({ entityType: 'TICKET', entityId: 'k2' }), 'station')).toBe('/station/tickets/k2');
  });

  it('never builds a path from an id it has not escaped', () => {
    expect(notificationLink(note({ entityType: 'TICKET', entityId: '../ops/users' }), 'company')).toBe('/company/tickets/..%2Fops%2Fusers');
  });
});

describe('notification params', () => {
  it('reads integers and codes only', () => {
    const n = note({ params: { amountPiastres: 125, alertType: 'LOW_BALANCE', bad: 1.5, text: 'hello world', html: '<b>' } });
    expect(intParam(n, 'amountPiastres')).toBe(125);
    expect(intParam(n, 'bad')).toBeUndefined();
    expect(codeParam(n, 'alertType')).toBe('LOW_BALANCE');
    expect(codeParam(n, 'text')).toBeUndefined();
    expect(codeParam(n, 'html')).toBeUndefined();
  });

  it('shows the badge as the API caps it', () => {
    expect(unreadBadge(0, false)).toBeUndefined();
    expect(unreadBadge(7, false)).toBe('7');
    expect(unreadBadge(99, true)).toBe('99+');
  });
});

describe('polite polling', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('asks at once, then every interval, and not while the page is hidden', async () => {
    let visible = true;
    let onChange: () => void = () => undefined;
    const run = vi.fn(() => Promise.resolve());
    const poller = createPoller({
      run,
      intervalMs: 60_000,
      isVisible: () => visible,
      onVisibilityChange: (listener) => {
        onChange = listener;
        return () => undefined;
      },
    });
    poller.start();
    expect(run).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(run).toHaveBeenCalledTimes(2);

    visible = false;
    onChange();
    await vi.advanceTimersByTimeAsync(180_000);
    expect(run).toHaveBeenCalledTimes(2);

    visible = true;
    onChange();
    expect(run).toHaveBeenCalledTimes(3);
    poller.stop();
    await vi.advanceTimersByTimeAsync(120_000);
    expect(run).toHaveBeenCalledTimes(3);
  });

  it('does not stack a request on one still on its way', async () => {
    let finish: () => void = () => undefined;
    const run = vi.fn(() => new Promise<void>((resolve) => (finish = resolve)));
    const poller = createPoller({ run, intervalMs: 1_000, isVisible: () => true, onVisibilityChange: () => () => undefined });
    poller.start();
    await vi.advanceTimersByTimeAsync(3_000);
    expect(run).toHaveBeenCalledTimes(1);
    finish();
    await vi.advanceTimersByTimeAsync(1_000);
    expect(run).toHaveBeenCalledTimes(2);
    poller.stop();
  });
});

describe('notifications store', () => {
  const fakeApi = (overrides: Partial<NotificationsApi> = {}): NotificationsApi => ({
    list: vi.fn(async () => ({ items: [note(), note({ id: 'n2', read: true })], nextCursor: 'c2' })),
    unreadCount: vi.fn(async () => ({ count: 3, capped: false })),
    markRead: vi.fn(async () => undefined),
    markAllRead: vi.fn(async () => undefined),
    ...overrides,
  });
  const manualPoller = (run: () => Promise<unknown>) => ({ start: () => void run(), stop: vi.fn(), refresh: () => void run() });

  it('polls once for every header that shows it and stops after the last leaves', async () => {
    const api = fakeApi();
    const stop = vi.fn();
    const store = createNotificationsStore(api, (run) => ({ start: () => void run(), stop, refresh: () => void run() }));
    const releaseDesktop = store.retain();
    const releaseMobile = store.retain();
    await Promise.resolve();
    expect(api.unreadCount).toHaveBeenCalledTimes(1);
    expect(store.getState().count).toBe(3);
    releaseDesktop();
    expect(stop).not.toHaveBeenCalled();
    releaseMobile();
    expect(stop).toHaveBeenCalledTimes(1);
  });

  it('marks one read at once and puts it back if the API refuses', async () => {
    const api = fakeApi({ markRead: vi.fn(async () => Promise.reject(new Error('offline'))) });
    const store = createNotificationsStore(api, manualPoller);
    store.retain();
    await store.openFeed();
    await Promise.resolve();
    expect(store.getState().items).toHaveLength(2);
    const pending = store.markRead('n1');
    expect(store.getState().items[0]?.read).toBe(true);
    expect(store.getState().count).toBe(2);
    await pending;
    expect(store.getState().items[0]?.read).toBe(false);
    expect(store.getState().count).toBe(3);
  });

  it('marks everything read and loads the next page with the cursor', async () => {
    const api = fakeApi();
    const store = createNotificationsStore(api, manualPoller);
    store.retain();
    await store.openFeed();
    await store.markAllRead();
    expect(api.markAllRead).toHaveBeenCalledTimes(1);
    expect(store.getState().items.every((item) => item.read)).toBe(true);
    await store.loadMore();
    expect(api.list).toHaveBeenLastCalledWith({ limit: 20, cursor: 'c2' });
    expect(store.getState().items).toHaveLength(4);
  });

  it('says so when the feed cannot load', async () => {
    const store = createNotificationsStore(fakeApi({ list: vi.fn(async () => Promise.reject(new Error('500'))) }), manualPoller);
    await store.openFeed();
    expect(store.getState()).toMatchObject({ error: true, loading: false });
  });
});
