import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { create } from 'zustand';
import { API_MODE } from '@/api';
import { useSession } from '@/auth/session-store';
import type { SessionUser } from '@/auth/types';
import { useLocale } from '@/i18n/useLocale';
import { companyApi, stationApi } from '@/live/api';
import { BRAND_NAME } from '@/lib/brand';
import { localize, type LocalizedText } from '@/lib/locale';
import { useDemoStore } from '@/mocks/store';
import type { PortalConfig } from './types';

/** Organisation names by scope id, fetched once per session for the header and menus. */
const useOrganizations = create<{ names: Record<string, LocalizedText | 'loading'> }>(() => ({ names: {} }));

function loadOrganization(user: SessionUser): void {
  const id = user.scope.id;
  if (!id || useOrganizations.getState().names[id]) return;
  useOrganizations.setState((state) => ({ names: { ...state.names, [id]: 'loading' } }));
  const request =
    user.scope.type === 'COMPANY'
      ? companyApi.profile().then((company) => ({ ar: company.nameAr, en: company.nameEn }))
      : stationApi.operator().then((operator) => ({ ar: operator.nameAr, en: operator.nameEn }));
  request.then(
    (name) => useOrganizations.setState((state) => ({ names: { ...state.names, [id]: name } })),
    () =>
      // Leave it unknown: the header then shows the portal name only, and a later mount tries again.
      useOrganizations.setState((state) => ({
        names: Object.fromEntries(Object.entries(state.names).filter(([key]) => key !== id)),
      })),
  );
}

export interface PortalIdentity {
  /** Signed in against the API (not the in-browser demo). */
  live: boolean;
  name: string;
  role: string;
  initials: string;
  /** The company, the station operator, or ZAWED itself; empty while it loads. */
  organization: string;
}

function initialsOf(fullName: string): string {
  const parts = fullName.trim().split(/\s+/).filter(Boolean);
  return parts
    .slice(0, 2)
    .map((part) => part[0])
    .join('')
    .toUpperCase();
}

/**
 * Who is using the portal. Against the API it is the signed-in account and its own organisation;
 * in the demo it is the perspective's persona. Never mixes the two: a real company user must not
 * see the demo company's name.
 */
export function usePortalIdentity(config: PortalConfig): PortalIdentity {
  const { t } = useTranslation(['shell', 'auth']);
  const { locale } = useLocale();
  const demoOrganization = useDemoStore((state) => config.organizationName(state.data, locale));
  const user = useSession((state) => state.user);
  const live = API_MODE === 'http' && user !== undefined;
  const scopeId = live ? user.scope.id : undefined;
  const organizationName = useOrganizations((state) => (scopeId ? state.names[scopeId] : undefined));

  useEffect(() => {
    if (live && user.scope.type !== 'PLATFORM') loadOrganization(user);
  }, [live, user]);

  if (!live) {
    return {
      live: false,
      name: localize(config.persona.name, locale),
      role: t(`shell:roles.${config.persona.role}`),
      initials: localize(config.persona.initials, locale),
      organization: demoOrganization,
    };
  }
  const organization =
    user.scope.type === 'PLATFORM'
      ? localize(BRAND_NAME, locale)
      : organizationName && organizationName !== 'loading'
        ? localize(organizationName, locale)
        : '';
  return {
    live: true,
    name: user.fullName,
    role: t(`auth:roles.${user.role}`),
    initials: initialsOf(user.fullName),
    organization,
  };
}
