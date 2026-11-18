import { CalendarClock, House, Landmark, LifeBuoy, Receipt, Settings, Smartphone } from 'lucide-react';
import { API_MODE } from '@/api';
import { CalendarDays } from 'lucide-react';
import { localize } from '@/lib/locale';
import { DEMO_CONTEXT, PERSONAS } from '@/mocks/personas';
import type { NavGroup, NavItem, PortalConfig } from '@/shell/types';

const base = '/station';

const items = {
  home: { id: 'home', path: base, icon: House },
  transactions: { id: 'transactions', path: `${base}/transactions`, icon: Receipt },
  calendar: { id: 'calendar', path: `${base}/activity`, icon: CalendarDays },
  devices: { id: 'devices', path: `${base}/devices`, icon: Smartphone },
  shifts: { id: 'shifts', path: `${base}/shifts`, icon: CalendarClock },
  settlements: { id: 'settlements', path: `${base}/settlements`, icon: Landmark },
  tickets: { id: 'tickets', path: `${base}/tickets`, icon: LifeBuoy },
  settings: { id: 'settings', path: `${base}/settings`, icon: Settings },
} satisfies Record<string, NavItem>;

export const stationPortal: PortalConfig = {
  id: 'station',
  basePath: base,
  groups: [
    { id: 'main', items: [items.home] },
    { id: 'operations', label: 'operations', items: [items.transactions, ...(API_MODE === 'http' ? [items.calendar] : []), items.shifts] },
    { id: 'network', label: 'network', items: [items.devices] },
    { id: 'finance', label: 'finance', items: [items.settlements] },
    ...(API_MODE === 'http' ? [{ id: 'support', label: 'support', items: [items.tickets] } satisfies NavGroup] : []),
    { id: 'settings', items: [items.settings] },
  ],
  mobileTabs: [items.home, items.transactions, items.devices, items.shifts],
  persona: PERSONAS.station,
  organizationName: (data, locale) => localize(data.stations[DEMO_CONTEXT.stationId]?.name, locale),
};
