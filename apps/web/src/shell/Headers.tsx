import { ArrowLeft, ChevronRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Icon, BrandLogo } from '@/design-system';
import { NotificationsBell } from './NotificationsBell';
import { ProfileMenu } from './ProfileMenu';
import type { PortalConfig } from './types';
import { usePortalLocation } from './use-portal-location';

/**
 * Mobile header: deliberately minimal. Primary screens show the logo; screens reached through
 * More show a back link to More. Language and theme live in the account menu.
 */
export function MobileHeader({ config }: { config: PortalConfig }) {
  const { t } = useTranslation(['navigation', 'shell']);
  const location = usePortalLocation(config);
  return (
    <header className="sticky top-0 z-20 flex h-14 items-center justify-between gap-2 border-b border-border bg-background/90 ps-2 pe-2 backdrop-blur-md md:hidden">
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
          <BrandLogo size="sm" variant="compact" />
        </Link>
      )}
      <div className="flex items-center gap-1">
        <NotificationsBell config={config} variant="fullscreen" />
        <ProfileMenu config={config} />
      </div>
    </header>
  );
}

/** Tablet and desktop header: page context (breadcrumb), notifications and account. */
export function DesktopHeader({ config }: { config: PortalConfig }) {
  const { t } = useTranslation(['navigation', 'shell']);
  const { item, group } = usePortalLocation(config);
  return (
    <header className="sticky top-0 z-20 hidden h-16 items-center gap-4 border-b border-border bg-background/90 px-6 backdrop-blur-md md:flex lg:px-8">
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
        <NotificationsBell config={config} variant="popover" />
        <ProfileMenu config={config} showName />
      </div>
    </header>
  );
}
