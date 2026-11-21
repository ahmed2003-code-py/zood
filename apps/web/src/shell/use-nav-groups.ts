import { API_MODE } from '@/api';
import { useCan } from '@/live/permissions';
import type { NavGroup, PortalConfig } from './types';

/**
 * The portal's navigation without the destinations the signed-in role cannot use (for example the
 * audit log for anyone but a Super Admin). The in-browser demo shows everything.
 */
export function useNavGroups(config: PortalConfig): NavGroup[] {
  const { can } = useCan();
  if (API_MODE !== 'http') return config.groups;
  return config.groups
    .map((group) => ({ ...group, items: group.items.filter((item) => !item.requires || can(item.requires)) }))
    .filter((group) => group.items.length > 0);
}
