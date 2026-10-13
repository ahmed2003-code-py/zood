import { LogOut, Settings } from 'lucide-react';
import { Popover } from 'radix-ui';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Icon, LanguageSwitcher, ThemeSwitcher } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { localize } from '@/lib/locale';
import { useDemoStore } from '@/mocks/store';
import type { PortalConfig } from './types';

export function Avatar({ initials, className }: { initials: string; className?: string }) {
  return (
    <span
      aria-hidden
      className={cn(
        'flex size-9 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-foreground',
        className,
      )}
    >
      {initials}
    </span>
  );
}

/**
 * Account menu: identity, display preferences (language, theme), settings and sign-out.
 * Language and theme live here rather than in the header row.
 */
export function ProfileMenu({ config, showName = false }: { config: PortalConfig; showName?: boolean }) {
  const { t } = useTranslation(['shell', 'settings']);
  const { locale } = useLocale();
  const organization = useDemoStore((state) => config.organizationName(state.data, locale));
  const name = localize(config.persona.name, locale);
  const role = t(`shell:roles.${config.persona.role}`);
  const menuLink = 'flex min-h-11 items-center gap-3 rounded-control px-3 text-sm font-medium transition-colors hover:bg-surface-sunken';

  return (
    <Popover.Root>
      <Popover.Trigger
        aria-label={t('shell:profile.open')}
        className="flex min-h-11 items-center gap-3 rounded-control px-1.5 transition-colors hover:bg-surface-sunken"
      >
        <Avatar initials={localize(config.persona.initials, locale)} />
        {showName && (
          <span className="hidden min-w-0 flex-col text-start lg:flex">
            <span className="truncate text-sm font-semibold">{name}</span>
            <span className="truncate text-xs text-muted">{role}</span>
          </span>
        )}
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content
          align="end"
          sideOffset={8}
          collisionPadding={16}
          className="z-50 w-[min(20rem,calc(100vw-2rem))] rounded-container border border-border bg-surface-elevated p-2 text-foreground shadow-elevated animate-[zood-fade-in_120ms_ease-out] focus:outline-none"
        >
          <div className="flex items-center gap-3 px-2 pt-2 pb-3">
            <Avatar initials={localize(config.persona.initials, locale)} className="size-11" />
            <div className="min-w-0">
              <p className="truncate font-semibold">{name}</p>
              <p className="truncate text-sm text-muted">
                {role} · {organization}
              </p>
            </div>
          </div>
          <div className="flex flex-col gap-3 border-y border-border px-2 py-3">
            <div className="flex flex-col gap-1.5">
              <span className="text-xs font-medium text-muted">{t('settings:display.language')}</span>
              <LanguageSwitcher className="w-full" />
            </div>
            <div className="flex flex-col gap-1.5">
              <span className="text-xs font-medium text-muted">{t('settings:display.appearance')}</span>
              <ThemeSwitcher className="w-full" />
            </div>
          </div>
          <div className="flex flex-col pt-1">
            <Popover.Close asChild>
              <Link to={`${config.basePath}/settings`} className={menuLink}>
                <Icon icon={Settings} size={18} className="text-muted" />
                {t('shell:profile.settings')}
              </Link>
            </Popover.Close>
            <Popover.Close asChild>
              <Link to="/demo" className={menuLink}>
                <Icon icon={LogOut} size={18} directional className="text-muted" />
                {t('shell:profile.signOut')}
              </Link>
            </Popover.Close>
          </div>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}
