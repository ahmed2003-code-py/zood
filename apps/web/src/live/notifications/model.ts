/**
 * Notifications carry no display text (b7-notifications-contract.md §1): a type, the record it
 * points at, and params that are ids, integers and codes. This module turns one into where it leads
 * in each portal and into the values its sentence needs. Kept free of React so it can be tested.
 */
import type { Notification, NotificationType } from '../api';

export type NotificationPortal = 'company' | 'station' | 'ops';

/** An integer param (piastres, millilitres), or undefined when absent or not a safe integer. */
export function intParam(notification: Notification, key: string): number | undefined {
  const value = notification.params[key];
  return typeof value === 'number' && Number.isSafeInteger(value) ? value : undefined;
}

/** A code param (an enum name), or undefined. Only A–Z, digits and underscores are accepted. */
export function codeParam(notification: Notification, key: string): string | undefined {
  const value = notification.params[key];
  return typeof value === 'string' && /^[A-Z0-9_]{1,64}$/.test(value) ? value : undefined;
}

/** The record a notification points at, in the portal that shows it. */
export function notificationLink(notification: Notification, portal: NotificationPortal): string {
  const { entityType, entityId } = notification;
  const id = encodeURIComponent(entityId);
  switch (portal) {
    case 'company':
      if (entityType === 'TICKET') return `/company/tickets/${id}`;
      if (entityType === 'COMPANY_WALLET' || entityType === 'TOPUP') return '/company/wallet';
      return '/company';
    case 'station':
      if (entityType === 'TICKET') return `/station/tickets/${id}`;
      if (entityType === 'PAYOUT' || entityType === 'STATION_BANK_ACCOUNT') return '/station/settlements';
      return '/station';
    case 'ops':
      if (entityType === 'TICKET') return `/ops/tickets/${id}`;
      if (entityType === 'ALERT') return `/ops/alerts/${id}`;
      if (entityType === 'TOPUP') return '/ops/topups?status=PENDING_APPROVAL';
      if (entityType === 'WALLET_ADJUSTMENT') return '/ops/topups?tab=adjustments';
      if (entityType === 'COMPANY_WALLET') return '/ops/companies';
      if (entityType === 'PAYOUT') return '/ops/settlements?tab=payouts';
      if (entityType === 'STATION_BANK_ACCOUNT') return '/ops/settlements?tab=bankChanges';
      return '/ops';
  }
}

export type NotificationTone = 'success' | 'warning' | 'danger' | 'info' | 'brand';

/** How a notification reads at a glance: good news, something to act on, or a problem. */
export const NOTIFICATION_TONE: Record<NotificationType, NotificationTone> = {
  WALLET_LOW_BALANCE: 'warning',
  TOPUP_CREDITED: 'success',
  TOPUP_REJECTED: 'danger',
  TICKET_ANSWERED: 'info',
  TOPUP_AWAITING_APPROVAL: 'brand',
  WALLET_ADJUSTMENT_AWAITING_APPROVAL: 'brand',
  HARD_ALERT_RAISED: 'danger',
  TICKET_OPENED: 'info',
  PAYOUT_POSTED: 'success',
  BANK_ACCOUNT_CHANGE_APPROVED: 'success',
  BANK_ACCOUNT_CHANGE_REJECTED: 'danger',
};

/** "99+" above the API's cap, nothing at zero. */
export function unreadBadge(count: number, capped: boolean): string | undefined {
  if (capped || count > 99) return '99+';
  return count > 0 ? String(count) : undefined;
}

export interface Poller {
  start: () => void;
  stop: () => void;
  /** Ask now (for example after "mark all read"); skipped while the page is hidden. */
  refresh: () => void;
}

/**
 * Polite polling: one request every `intervalMs` while the page is visible, none while it is hidden,
 * and one straight away when it becomes visible again. A request still on its way is not repeated.
 */
export function createPoller({
  run,
  intervalMs,
  isVisible = () => document.visibilityState === 'visible',
  onVisibilityChange = (listener) => {
    document.addEventListener('visibilitychange', listener);
    return () => document.removeEventListener('visibilitychange', listener);
  },
}: {
  run: () => Promise<unknown>;
  intervalMs: number;
  isVisible?: () => boolean;
  onVisibilityChange?: (listener: () => void) => () => void;
}): Poller {
  let timer: ReturnType<typeof setInterval> | undefined;
  let unsubscribe: (() => void) | undefined;
  let inFlight = false;

  const tick = () => {
    if (inFlight || !isVisible()) return;
    inFlight = true;
    run()
      .catch(() => undefined)
      .finally(() => {
        inFlight = false;
      });
  };
  const arm = () => {
    if (timer === undefined) timer = setInterval(tick, intervalMs);
  };
  const disarm = () => {
    if (timer !== undefined) clearInterval(timer);
    timer = undefined;
  };

  return {
    start: () => {
      if (unsubscribe) return;
      unsubscribe = onVisibilityChange(() => {
        if (isVisible()) {
          tick();
          arm();
        } else {
          disarm();
        }
      });
      tick();
      if (isVisible()) arm();
    },
    stop: () => {
      disarm();
      unsubscribe?.();
      unsubscribe = undefined;
    },
    refresh: tick,
  };
}
