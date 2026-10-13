import { ArrowLeft, Bell, ChevronRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Icon, ZoodWordmark } from '@/design-system';
import { useDemoStore } from '@/mocks/store';
import { ProfileMenu } from './ProfileMenu';
import type { PortalConfig } from './types';
import { usePortalLocation } from './use-portal-location';

function NotificationsLink({ config }: { config: PortalConfig }) {
  const { t } = useTranslation('shell');
  const count = useDemoStore((state) => config.openAlertCount?.(state.data) ?? 0);
  if (!config.alertsPath) return null;
  return (
    <Link
      to={config.alertsPath}
      aria-label={t('notifications')}
      title={t('notifications')}
      className="relative flex size-11 items-center justify-center rounded-control text-foreground transition-colors hover:bg-surface-sunken"
    >
      <Icon icon={Bell} />
      {count > 0 && (
        <span className="absolute end-1.5 top-1.5 flex min-w-5 items-center justify-center rounded-full bg-accent px-1 text-[0.6875rem] leading-5 font-semibold text-accent-foreground tabular-nums">
          {count}
        </span>
      )}
    </Link>
  );
}

/**
 * Mobile header: deliberately minimal. Primary screens show the logo; screens reached through
 * More show a back link to More. Language and theme live in the account menu.
 */
export function MobileHeader({ config }: { config: PortalConfig }) {
  const { t } = useTranslation(['navigation', 'shell']);
  const location = usePortalLocation(config);
  return (
    <header className="sticky top-0 z-20 flex h-14 items-center justify-between gap-2 border-b border-border bg-background ps-2 pe-2 md:hidden">
      {location.isSecondary ? (
        <Link
          to={location.morePath}
          className="flex min-h-11 items-center gap-1.5 rounded-control px-2 text-sm font-medium transition-colors hover:bg-surface-sunken"
        >
          <Icon icon={ArrowLeft} directional />
          {t('navigation:items.more')}
        </Link>
      ) : (
        <Link to={config.basePath} className="flex min-h-11 items-center px-2" aria-label={t('navigation:items.home')}>
          <ZoodWordmark size="sm" showLatin={false} />
        </Link>
      )}
      <ProfileMenu config={config} />
    </header>
  );
}

/** Tablet and desktop header: page context (breadcrumb), notifications and account. */
export function DesktopHeader({ config }: { config: PortalConfig }) {
  const { t } = useTranslation(['navigation', 'shell']);
  const { item, group } = usePortalLocation(config);
  return (
    <header className="sticky top-0 z-20 hidden h-16 items-center gap-4 border-b border-border bg-background px-6 md:flex lg:px-8">
      <nav aria-label={t('shell:breadcrumb')} className="min-w-0 flex-1">
        <ol className="flex items-center gap-1.5 text-sm">
          <li className="text-muted">{t(`navigation:portals.${config.id}`)}</li>
          {group?.label && (
            <li className="flex items-center gap-1.5 text-muted">
              <Icon icon={ChevronRight} directional size={14} />
              {t(`navigation:groups.${group.label}`)}
            </li>
          )}
          {item && item.path !== config.basePath && (
            <li aria-current="page" className="flex min-w-0 items-center gap-1.5 font-medium">
              <Icon icon={ChevronRight} directional size={14} className="text-muted" />
              <span className="truncate">{t(`navigation:items.${item.id}`)}</span>
            </li>
          )}
        </ol>
      </nav>
      <div className="flex items-center gap-1">
        <NotificationsLink config={config} />
        <ProfileMenu config={config} showName />
      </div>
    </header>
  );
}
