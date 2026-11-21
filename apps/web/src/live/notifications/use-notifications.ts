/** React side of the shared notifications store: one poller however many headers show the bell. */
import { useEffect, useSyncExternalStore } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocale } from '@/i18n/useLocale';
import { formatMoney } from '@/lib/format';
import { notificationsApi, type Notification } from '../api';
import { codeParam, intParam } from './model';
import { createNotificationsStore } from './store';

const store = createNotificationsStore({
  list: (q) => notificationsApi.list(q),
  unreadCount: () => notificationsApi.unreadCount(),
  markRead: (id) => notificationsApi.markRead(id),
  markAllRead: () => notificationsApi.markAllRead(),
});

export function useNotifications(enabled: boolean) {
  useEffect(() => (enabled ? store.retain() : undefined), [enabled]);
  const state = useSyncExternalStore(store.subscribe, store.getState, store.getState);
  return { ...state, openFeed: store.openFeed, loadMore: store.loadMore, markRead: store.markRead, markAllRead: store.markAllRead };
}

/** The sentence a notification makes in the language on screen, from its type and params only. */
export function useNotificationText(): (notification: Notification) => { title: string; body: string } {
  const { t } = useTranslation(['portal', 'live']);
  const { locale } = useLocale();
  return (notification) => {
    const money = (key: string) => {
      const value = intParam(notification, key);
      return value === undefined ? t('portal:notifications.someAmount') : formatMoney(value, locale);
    };
    const ticketType = codeParam(notification, 'ticketType');
    const alertType = codeParam(notification, 'alertType');
    const severity = codeParam(notification, 'severity');
    const values = {
      amount: money('amountPiastres'),
      balance: money('balancePiastres'),
      threshold: money('thresholdPiastres'),
      ticketType: ticketType ? t(`live:tickets.type.${ticketType}` as 'live:tickets.type.OTHER', { defaultValue: ticketType }) : t('portal:notifications.someType'),
      alertType: alertType ? t(`live:alerts.type.${alertType}` as 'live:alerts.type.LOW_BALANCE', { defaultValue: alertType }) : t('portal:notifications.someAlert'),
      severity: severity ? t(`live:alerts.severity.${severity}` as 'live:alerts.severity.WARNING', { defaultValue: severity }) : '',
    };
    return {
      title: t(`portal:notifications.types.${notification.type}.title`, values),
      body: t(`portal:notifications.types.${notification.type}.body`, values),
    };
  };
}
