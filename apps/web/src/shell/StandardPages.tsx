import { ChevronRight, LogOut } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { loginPath } from '@/auth/portals';
import { useSession } from '@/auth/session-store';
import { AppearanceSettings, Icon } from '@/design-system';
import { ListPanel, PageHeader, PageSection } from './page';
import { Avatar } from './ProfileMenu';
import type { PortalConfig } from './types';
import { usePortalIdentity } from './use-portal-identity';
import { isItemActive } from './use-portal-location';

const rowClass = 'flex min-h-14 items-center gap-3 px-4 text-start transition-colors hover:bg-surface-sunken';

/** Mobile "More": every destination that is not a bottom-navigation tab, grouped as in the sidebar. */
export function MorePage({ config }: { config: PortalConfig }) {
  const { t } = useTranslation(['navigation', 'shell']);
  const navigate = useNavigate();
  const identity = usePortalIdentity(config);
  const { door, signOut } = useSession();
  // Against the API this ends the session; in the demo it returns to the perspective launcher.
  const handleSignOut = async () => {
    if (!identity.live) {
      navigate('/demo');
      return;
    }
    await signOut();
    navigate(loginPath(door), { replace: true });
  };
  // Skip destinations already reachable from a tab (including tabs that cover several screens).
  const coveredByTab = (path: string) => config.mobileTabs.some((tab) => isItemActive(tab, path, config.basePath));
  const visible = config.groups
    .map((group) => ({ ...group, items: group.items.filter((item) => !coveredByTab(item.path)) }))
    .filter((group) => group.items.length > 0);
  // Labeled groups keep their heading; unlabeled single destinations are gathered in one final section.
  const groups = [
    ...visible.filter((group) => group.label),
    { id: 'other', label: undefined, items: visible.filter((group) => !group.label).flatMap((group) => group.items) },
  ].filter((group) => group.items.length > 0);

  return (
    <>
      <PageHeader title={t('navigation:items.more')} />
      <div className="flex flex-col gap-6">
        <ListPanel>
          <Link to={`${config.basePath}/settings`} className={rowClass}>
            <Avatar initials={identity.initials} className="size-11" />
            <span className="flex min-w-0 flex-1 flex-col">
              <span className="truncate font-semibold">{identity.name}</span>
              <span className="truncate text-sm text-muted">{identity.role}</span>
            </span>
            <Icon icon={ChevronRight} directional size={18} className="text-muted" />
          </Link>
        </ListPanel>
        {groups.map((group) => (
          <PageSection key={group.id} title={group.label ? t(`navigation:groups.${group.label}`) : undefined}>
            <ListPanel>
              <ul className="divide-y divide-border">
                {group.items.map((item) => (
                  <li key={item.id}>
                    <Link to={item.path} className={rowClass}>
                      <span className="flex size-9 items-center justify-center rounded-control bg-primary-subtle text-primary-subtle-foreground">
                        <Icon icon={item.icon} size={18} />
                      </span>
                      <span className="flex-1 font-medium">{t(`navigation:items.${item.id}`)}</span>
                      <Icon icon={ChevronRight} directional size={18} className="text-muted" />
                    </Link>
                  </li>
                ))}
              </ul>
            </ListPanel>
          </PageSection>
        ))}
        <ListPanel>
          <button type="button" onClick={handleSignOut} className={`${rowClass} w-full`}>
            <Icon icon={LogOut} directional className="text-muted" />
            <span className="flex-1 font-medium">{t('shell:profile.signOut')}</span>
          </button>
        </ListPanel>
      </div>
    </>
  );
}

/** Settings: account (read-only in the demo) and display preferences. */
export function SettingsPage({ config }: { config: PortalConfig }) {
  const { t } = useTranslation(['navigation', 'shell', 'settings']);
  const identity = usePortalIdentity(config);
  const rows = [
    [t('shell:settings.name'), identity.name],
    [t('shell:settings.role'), identity.role],
    [t('shell:settings.organization'), identity.organization],
  ];
  return (
    <>
      <PageHeader title={t('navigation:items.settings')} />
      <div className="grid max-w-3xl gap-6">
        <PageSection title={t('shell:settings.account')}>
          <ListPanel>
            <dl className="divide-y divide-border">
              {rows.map(([label, value]) => (
                <div key={label} className="flex items-center justify-between gap-4 px-4 py-3 text-sm">
                  <dt className="text-muted">{label}</dt>
                  <dd className="truncate font-medium">{value}</dd>
                </div>
              ))}
            </dl>
          </ListPanel>
        </PageSection>
        <AppearanceSettings />
      </div>
    </>
  );
}
