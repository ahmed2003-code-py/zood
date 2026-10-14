import { Menu, X } from 'lucide-react';
import { Dialog } from 'radix-ui';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { NavLink } from 'react-router';
import { Icon, IconButton, BrandLogo } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { useDemoStore } from '@/mocks/store';
import type { NavItem, PortalConfig } from './types';

function SidebarLink({ item, basePath, onNavigate }: { item: NavItem; basePath: string; onNavigate?: () => void }) {
  const { t } = useTranslation('navigation');
  return (
    <NavLink
      to={item.path}
      end={item.path === basePath}
      onClick={onNavigate}
      className={({ isActive }) =>
        cn(
          'relative flex min-h-10 items-center gap-3 rounded-control px-3 text-sm font-medium transition-colors',
          isActive
            ? 'bg-sidebar-active text-sidebar-active-foreground'
            : 'text-sidebar-foreground hover:bg-sidebar-active hover:text-sidebar-active-foreground',
        )
      }
    >
      {({ isActive }) => (
        <>
          {isActive && <span aria-hidden className="absolute inset-y-2 start-0 w-[3px] rounded-full bg-accent" />}
          <Icon icon={item.icon} size={18} />
          <span className="truncate">{t(`items.${item.id}`)}</span>
        </>
      )}
    </NavLink>
  );
}

/** Grouped navigation on the brand-green surface. Used by the desktop sidebar and the tablet drawer. */
export function SidebarNav({ config, onNavigate }: { config: PortalConfig; onNavigate?: () => void }) {
  const { t } = useTranslation(['navigation', 'shell']);
  const { locale } = useLocale();
  const organization = useDemoStore((state) => config.organizationName(state.data, locale));
  return (
    <div className="flex h-full w-full flex-col bg-sidebar text-sidebar-foreground">
      <div className="px-6 pt-6 pb-5">
        <BrandLogo size="md" tone="inverse" />
      </div>
      <div className="mx-3 mb-4 rounded-control border border-sidebar-active px-3 py-2.5">
        <p className="text-xs text-sidebar-muted">{t(`navigation:portals.${config.id}`)}</p>
        <p className="truncate text-sm font-semibold text-sidebar-active-foreground">{organization}</p>
      </div>
      <nav aria-label={t('shell:mainNavigation')} className="flex-1 overflow-y-auto px-3 pb-6">
        <div className="flex flex-col gap-5">
          {config.groups.map((group) => (
            <div key={group.id}>
              {group.label && <p className="px-3 pb-1.5 text-xs font-medium text-sidebar-muted">{t(`navigation:groups.${group.label}`)}</p>}
              <ul className="flex flex-col gap-0.5">
                {group.items.map((item) => (
                  <li key={item.id}>
                    <SidebarLink item={item} basePath={config.basePath} onNavigate={onNavigate} />
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
      </nav>
    </div>
  );
}

/** Tablet (768–1023px): icon rail with a button that opens the full grouped navigation in a drawer. */
export function NavRail({ config }: { config: PortalConfig }) {
  const { t } = useTranslation(['navigation', 'shell', 'common']);
  const [open, setOpen] = useState(false);
  const items = config.groups.flatMap((group) => group.items);
  return (
    <div className="flex h-full w-full flex-col items-center gap-2 bg-sidebar py-4">
      <Dialog.Root open={open} onOpenChange={setOpen}>
        <Dialog.Trigger asChild>
          <IconButton
            icon={Menu}
            label={t('shell:openNavigation')}
            className="text-sidebar-foreground hover:bg-sidebar-active hover:text-sidebar-active-foreground"
          />
        </Dialog.Trigger>
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 z-40 bg-overlay animate-[zood-fade-in_150ms_ease-out]" />
          <Dialog.Content className="fixed inset-y-0 start-0 z-50 w-72 shadow-elevated animate-[zood-sheet-from-start_220ms_ease-out] focus:outline-none">
            <Dialog.Title className="sr-only">{t('shell:mainNavigation')}</Dialog.Title>
            <Dialog.Description className="sr-only">{t(`navigation:portals.${config.id}`)}</Dialog.Description>
            <SidebarNav config={config} onNavigate={() => setOpen(false)} />
            <Dialog.Close asChild>
              <IconButton
                icon={X}
                label={t('common:a11y.closeDialog')}
                className="absolute end-3 top-4 text-sidebar-foreground hover:bg-sidebar-active hover:text-sidebar-active-foreground"
              />
            </Dialog.Close>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
      <nav aria-label={t('shell:mainNavigation')} className="flex w-full flex-1 flex-col items-center gap-1 overflow-y-auto px-2">
        {items.map((item) => (
          <NavLink
            key={item.id}
            to={item.path}
            end={item.path === config.basePath}
            aria-label={t(`navigation:items.${item.id}`)}
            title={t(`navigation:items.${item.id}`)}
            className={({ isActive }) =>
              cn(
                'relative flex size-12 items-center justify-center rounded-control transition-colors',
                isActive
                  ? 'bg-sidebar-active text-sidebar-active-foreground'
                  : 'text-sidebar-foreground hover:bg-sidebar-active hover:text-sidebar-active-foreground',
              )
            }
          >
            {({ isActive }) => (
              <>
                {isActive && <span aria-hidden className="absolute inset-y-3 start-0 w-[3px] rounded-full bg-accent" />}
                <Icon icon={item.icon} />
              </>
            )}
          </NavLink>
        ))}
      </nav>
    </div>
  );
}
