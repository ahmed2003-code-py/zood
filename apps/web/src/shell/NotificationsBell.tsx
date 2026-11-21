/**
 * The notifications bell in every portal's header. Against the API it shows the unread count
 * (polled once a minute while the tab is visible) and opens the feed: a popover from 768 px, a
 * full-screen list on phones. Each notification leads to its record. In the in-browser demo it
 * stays a link to the alerts screen with the demo's open-alert count.
 */
import {
  Banknote,
  Bell,
  CircleCheck,
  CircleX,
  Hourglass,
  Landmark,
  LifeBuoy,
  MessageSquareReply,
  ShieldAlert,
  Wallet,
  X,
  type LucideIcon,
} from 'lucide-react';
import { Dialog, Popover } from 'radix-ui';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { API_MODE } from '@/api';
import { useSession } from '@/auth/session-store';
import { Button, DateTimeText, EmptyState, Icon, IconButton, Skeleton } from '@/design-system';
import type { Notification, NotificationType } from '@/live/api';
import { NOTIFICATION_TONE, notificationLink, unreadBadge, type NotificationTone } from '@/live/notifications/model';
import { useNotificationText, useNotifications } from '@/live/notifications/use-notifications';
import { useDemoStore } from '@/mocks/store';
import { cn } from '@/lib/cn';
import type { PortalConfig } from './types';

const ICON: Record<NotificationType, LucideIcon> = {
  WALLET_LOW_BALANCE: Wallet,
  TOPUP_CREDITED: CircleCheck,
  TOPUP_REJECTED: CircleX,
  TICKET_ANSWERED: MessageSquareReply,
  TOPUP_AWAITING_APPROVAL: Hourglass,
  WALLET_ADJUSTMENT_AWAITING_APPROVAL: Hourglass,
  HARD_ALERT_RAISED: ShieldAlert,
  TICKET_OPENED: LifeBuoy,
  PAYOUT_POSTED: Banknote,
  BANK_ACCOUNT_CHANGE_APPROVED: Landmark,
  BANK_ACCOUNT_CHANGE_REJECTED: Landmark,
};

const TONE_CLASS: Record<NotificationTone, string> = {
  success: 'bg-success-subtle text-success',
  warning: 'bg-warning-subtle text-warning',
  danger: 'bg-danger-subtle text-danger',
  info: 'bg-info-subtle text-info',
  brand: 'bg-primary-subtle text-primary-subtle-foreground',
};

const triggerClass =
  'relative flex size-10 items-center justify-center rounded-control text-muted transition-colors hover:bg-surface-sunken hover:text-foreground active:bg-surface-hover pointer-coarse:size-11';

function Badge({ text }: { text?: string }) {
  if (!text) return null;
  return (
    <span className="absolute end-1 top-1 flex min-w-4.5 items-center justify-center rounded-full bg-accent px-1 text-[0.625rem] leading-[1.125rem] font-semibold text-accent-foreground tabular-nums ring-2 ring-background">
      {text}
    </span>
  );
}

/** The demo keeps its alerts link with the demo's own count. */
function DemoNotificationsLink({ config }: { config: PortalConfig }) {
  const { t } = useTranslation('shell');
  const count = useDemoStore((state) => config.openAlertCount?.(state.data) ?? 0);
  if (!config.alertsPath) return null;
  return (
    <Link to={config.alertsPath} aria-label={t('notifications')} title={t('notifications')} className={triggerClass}>
      <Icon icon={Bell} />
      <Badge text={count > 0 ? String(count) : undefined} />
    </Link>
  );
}

function NotificationItem({ notification, portal, onOpen }: { notification: Notification; portal: PortalConfig['id']; onOpen: (to: string) => void }) {
  const { t } = useTranslation('portal');
  const text = useNotificationText();
  const { title, body } = text(notification);
  const tone = NOTIFICATION_TONE[notification.type];
  return (
    <li>
      <button
        type="button"
        onClick={() => onOpen(notificationLink(notification, portal))}
        className={cn(
          'flex w-full items-start gap-3 px-4 py-3 text-start transition-colors duration-150 hover:bg-surface-hover active:bg-surface-sunken',
          !notification.read && 'bg-primary-subtle/40',
        )}
      >
        <span className={cn('mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-control', TONE_CLASS[tone])}>
          <Icon icon={ICON[notification.type]} size={18} />
        </span>
        <span className="flex min-w-0 flex-1 flex-col gap-0.5">
          <span className={cn('text-sm [overflow-wrap:anywhere]', notification.read ? 'font-medium' : 'font-semibold')}>{title}</span>
          <span className="text-label text-pretty text-muted [overflow-wrap:anywhere]">{body}</span>
          <DateTimeText value={notification.createdAt} variant="relative" className="text-xs text-muted" />
        </span>
        {!notification.read && (
          <span className="mt-2 size-2 shrink-0 rounded-full bg-primary">
            <span className="sr-only">{t('notifications.unread')}</span>
          </span>
        )}
      </button>
    </li>
  );
}

/** The feed itself: shared by the popover and the full-screen list. */
function Feed({ portal, onNavigate }: { portal: PortalConfig['id']; onNavigate: () => void }) {
  const { t } = useTranslation('portal');
  const navigate = useNavigate();
  const feed = useNotifications(true);
  const open = (notification: Notification, to: string) => {
    void feed.markRead(notification.id);
    onNavigate();
    navigate(to);
  };
  if (feed.loading && feed.items.length === 0) {
    return (
      <div aria-busy className="flex flex-col gap-3 p-4">
        {[0, 1, 2].map((index) => (
          <div key={index} className="flex gap-3">
            <Skeleton className="size-9 shrink-0 rounded-control" />
            <div className="flex flex-1 flex-col gap-2">
              <Skeleton className="h-3.5 w-2/3" />
              <Skeleton className="h-3 w-full" />
            </div>
          </div>
        ))}
      </div>
    );
  }
  if (feed.error && feed.items.length === 0) {
    return (
      <EmptyState
        tone="danger"
        title={t('notifications.error')}
        description={t('notifications.errorHint')}
        action={
          <Button variant="secondary" size="sm" onClick={() => void feed.openFeed()}>
            {t('notifications.retry')}
          </Button>
        }
      />
    );
  }
  if (feed.items.length === 0) {
    return <EmptyState icon={Bell} title={t('notifications.empty')} description={t('notifications.emptyHint')} />;
  }
  return (
    <>
      <ul className="divide-y divide-border">
        {feed.items.map((notification) => (
          <NotificationItem key={notification.id} notification={notification} portal={portal} onOpen={(to) => open(notification, to)} />
        ))}
      </ul>
      {feed.nextCursor && (
        <div className="flex justify-center border-t border-border p-2">
          <Button variant="ghost" size="sm" loading={feed.loadingMore} onClick={() => void feed.loadMore()}>
            {t('notifications.more')}
          </Button>
        </div>
      )}
    </>
  );
}

function FeedHeader({ onClose }: { onClose?: () => void }) {
  const { t } = useTranslation(['portal', 'common']);
  const feed = useNotifications(true);
  const anyUnread = feed.count > 0 || feed.items.some((item) => !item.read);
  return (
    <div className="flex min-h-14 items-center justify-between gap-2 border-b border-border px-4">
      <h2 className="text-base font-semibold">{t('portal:notifications.title')}</h2>
      <div className="flex items-center gap-1">
        {anyUnread && (
          <Button variant="ghost" size="sm" onClick={() => void feed.markAllRead()}>
            {t('portal:notifications.markAll')}
          </Button>
        )}
        {onClose && <IconButton icon={X} label={t('portal:notifications.close')} onClick={onClose} />}
      </div>
    </div>
  );
}

function useTrigger() {
  const { t } = useTranslation('portal');
  const feed = useNotifications(true);
  const badge = unreadBadge(feed.count, feed.capped);
  const label = feed.count > 0 ? t('notifications.openUnread', { count: feed.count }) : t('notifications.open');
  return { feed, badge, label };
}

/** Desktop and tablet: a popover under the bell. */
function PopoverBell({ config }: { config: PortalConfig }) {
  const [open, setOpen] = useState(false);
  const { feed, badge, label } = useTrigger();
  return (
    <Popover.Root
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (next) void feed.openFeed();
      }}
    >
      <Popover.Trigger aria-label={label} title={label} className={triggerClass}>
        <Icon icon={Bell} />
        <Badge text={badge} />
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content
          align="end"
          sideOffset={8}
          collisionPadding={16}
          className="z-50 flex max-h-[min(34rem,calc(100dvh-6rem))] w-[min(24rem,calc(100vw-2rem))] flex-col overflow-clip rounded-container border border-border bg-surface-elevated text-foreground shadow-elevated animate-[zood-fade-in_120ms_ease-out] focus:outline-none"
        >
          <FeedHeader />
          <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain">
            <Feed portal={config.id} onNavigate={() => setOpen(false)} />
          </div>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

/** Phones: the list takes the whole screen, with its own close button. */
function FullScreenBell({ config }: { config: PortalConfig }) {
  const { t } = useTranslation('portal');
  const [open, setOpen] = useState(false);
  const { feed, badge, label } = useTrigger();
  return (
    <Dialog.Root
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (next) void feed.openFeed();
      }}
    >
      <Dialog.Trigger aria-label={label} className={triggerClass}>
        <Icon icon={Bell} />
        <Badge text={badge} />
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Content className="fixed inset-0 z-50 flex flex-col bg-background pt-[env(safe-area-inset-top)] pb-[env(safe-area-inset-bottom)] text-foreground animate-[zood-sheet-from-bottom_240ms_cubic-bezier(0.22,1,0.36,1)] focus:outline-none">
          <Dialog.Title className="sr-only">{t('notifications.title')}</Dialog.Title>
          <Dialog.Description className="sr-only">{label}</Dialog.Description>
          <div className="bg-surface">
            <FeedHeader onClose={() => setOpen(false)} />
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain bg-surface">
            <Feed portal={config.id} onNavigate={() => setOpen(false)} />
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

export function NotificationsBell({ config, variant }: { config: PortalConfig; variant: 'popover' | 'fullscreen' }) {
  const signedIn = useSession((state) => state.user !== undefined);
  if (API_MODE !== 'http') return <DemoNotificationsLink config={config} />;
  if (!signedIn) return null;
  return variant === 'popover' ? <PopoverBell config={config} /> : <FullScreenBell config={config} />;
}
