import type { Dataset } from '@/domain/dataset';
import type { LucideIcon } from '@/design-system';
import type navigation from '@/i18n/locales/en/navigation.json';
import type { Locale } from '@/lib/locale';
import type { Persona } from '@/mocks/personas';

export type NavItemKey = keyof typeof navigation.items;
export type NavGroupKey = keyof typeof navigation.groups;
export type PortalId = 'company' | 'station' | 'ops';

export interface NavItem {
  /** Label key in the navigation namespace (items). */
  id: NavItemKey;
  /** Absolute path, e.g. /company/wallet. */
  path: string;
  icon: LucideIcon;
  /** Extra path prefixes that also mark this item active (e.g. mobile "Network" covers stations, devices, sync). */
  matches?: string[];
}

export interface NavGroup {
  id: string;
  label?: NavGroupKey;
  items: NavItem[];
}

/**
 * Navigation definition for one portal. The same config drives the desktop sidebar, the tablet
 * rail and drawer, the mobile bottom navigation and the More screen.
 */
export interface PortalConfig {
  id: PortalId;
  basePath: string;
  groups: NavGroup[];
  /** Exactly four mobile destinations; "More" is added as the fifth. */
  mobileTabs: [NavItem, NavItem, NavItem, NavItem];
  persona: Persona;
  organizationName: (data: Dataset, locale: Locale) => string;
  /** Target of the header notifications button (omitted when the portal has no alerts screen). */
  alertsPath?: string;
  openAlertCount?: (data: Dataset) => number;
}
