import { Ellipsis } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Icon, type LucideIcon } from '@/design-system';
import { cn } from '@/lib/cn';
import type { NavItemKey, PortalConfig } from './types';
import { usePortalLocation } from './use-portal-location';

/** Mobile (<768px): four primary destinations plus More. */
export function BottomNav({ config }: { config: PortalConfig }) {
  const { t } = useTranslation(['navigation', 'shell']);
  const location = usePortalLocation(config);
  const tabs: Array<{ id: NavItemKey; path: string; icon: LucideIcon }> = [
    ...config.mobileTabs,
    { id: 'more', path: location.morePath, icon: Ellipsis },
  ];
  return (
    <nav
      data-shell="bottom-nav"
      aria-label={t('shell:mainNavigation')}
      className="fixed inset-x-0 bottom-0 z-30 border-t border-border bg-surface pb-[env(safe-area-inset-bottom)] md:hidden"
    >
      <ul className="mx-auto flex max-w-lg">
        {tabs.map((tab) => {
          const active = location.activeTab === tab.id;
          return (
            <li key={tab.id} className="flex-1">
              <Link
                to={tab.path}
                aria-current={active ? 'page' : undefined}
                className="flex min-h-16 flex-col items-center justify-center gap-1 px-1 text-xs font-medium"
              >
                <span
                  className={cn(
                    'flex h-8 w-14 items-center justify-center rounded-full transition-colors',
                    active ? 'bg-primary-subtle text-primary-subtle-foreground' : 'text-muted',
                  )}
                >
                  <Icon icon={tab.icon} size={22} />
                </span>
                <span className={cn('max-w-full truncate', active ? 'text-foreground' : 'text-muted')}>
                  {t(`navigation:items.${tab.id}`)}
                </span>
              </Link>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
