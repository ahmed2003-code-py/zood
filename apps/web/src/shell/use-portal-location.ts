import { useLocation } from 'react-router';
import type { NavGroup, NavItem, PortalConfig } from './types';

function matchesPath(pathname: string, path: string, exact: boolean): boolean {
  if (pathname === path) return true;
  return !exact && pathname.startsWith(`${path}/`);
}

export function isItemActive(item: NavItem, pathname: string, basePath: string): boolean {
  const exact = item.path === basePath;
  return matchesPath(pathname, item.path, exact) || (item.matches ?? []).some((path) => matchesPath(pathname, path, false));
}

export interface PortalLocation {
  item?: NavItem;
  group?: NavGroup;
  morePath: string;
  settingsPath: string;
  /** True on the More screen itself. */
  isMore: boolean;
  /** Active mobile tab id, or "more" for any screen reached through More. */
  activeTab: string;
  /** Screens reached through More get a back button on mobile. */
  isSecondary: boolean;
}

export function usePortalLocation(config: PortalConfig): PortalLocation {
  const { pathname } = useLocation();
  const morePath = `${config.basePath}/more`;
  const settingsPath = `${config.basePath}/settings`;
  let item: NavItem | undefined;
  let group: NavGroup | undefined;
  for (const candidate of config.groups) {
    const found = candidate.items.find((navItem) => isItemActive(navItem, pathname, config.basePath));
    if (found) {
      item = found;
      group = candidate;
      break;
    }
  }
  const isMore = pathname === morePath;
  const tab = config.mobileTabs.find((navItem) => isItemActive(navItem, pathname, config.basePath));
  return {
    item,
    group,
    morePath,
    settingsPath,
    isMore,
    activeTab: tab?.id ?? 'more',
    isSecondary: !tab && !isMore,
  };
}
