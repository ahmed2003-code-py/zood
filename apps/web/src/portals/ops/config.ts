import {
  Banknote,
  CircleDollarSign,
  Building2,
  Fuel,
  House,
  Landmark,
  Network,
  Receipt,
  RefreshCw,
  ScrollText,
  Settings,
  ShieldAlert,
  Smartphone,
  UsersRound,
} from 'lucide-react';
import { API_MODE } from '@/api';
import { BRAND_NAME } from '@/lib/brand';
import { localize } from '@/lib/locale';
import { PERSONAS } from '@/mocks/personas';
import type { NavItem, PortalConfig } from '@/shell/types';

const base = '/ops';

const items = {
  home: { id: 'home', path: base, icon: House },
  companies: { id: 'companies', path: `${base}/companies`, icon: Building2 },
  topups: { id: 'topups', path: `${base}/topups`, icon: Banknote },
  stations: { id: 'stations', path: `${base}/stations`, icon: Fuel },
  devices: { id: 'devices', path: `${base}/devices`, icon: Smartphone },
  sync: { id: 'sync', path: `${base}/sync`, icon: RefreshCw },
  transactions: { id: 'transactions', path: `${base}/transactions`, icon: Receipt },
  alerts: { id: 'alerts', path: `${base}/alerts`, icon: ShieldAlert },
  settlements: { id: 'settlements', path: `${base}/settlements`, icon: Landmark },
  audit: { id: 'audit', path: `${base}/audit`, icon: ScrollText },
  settings: { id: 'settings', path: `${base}/settings`, icon: Settings },
  users: { id: 'users', path: `${base}/users`, icon: UsersRound },
  prices: { id: 'prices', path: `${base}/prices`, icon: CircleDollarSign },
} satisfies Record<string, NavItem>;

/** Mobile "Network" tab covers stations, devices and sync (sub-tabs on the screens). */
const networkTab: NavItem = { id: 'network', path: items.stations.path, icon: Network, matches: [items.devices.path, items.sync.path] };

export const opsPortal: PortalConfig = {
  id: 'ops',
  basePath: base,
  groups: [
    { id: 'main', items: [items.home] },
    { id: 'customers', label: 'customers', items: [items.companies, items.topups] },
    { id: 'network', label: 'network', items: [items.stations, items.devices, items.sync, ...(API_MODE === 'http' ? [items.prices] : [])] },
    { id: 'transactions', items: [items.transactions] },
    { id: 'risk', label: 'risk', items: [items.alerts] },
    { id: 'finance', label: 'finance', items: [items.settlements] },
    { id: 'administration', label: 'administration', items: [...(API_MODE === 'http' ? [items.users] : []), items.audit] },
    { id: 'settings', items: [items.settings] },
  ],
  mobileTabs: [items.home, items.companies, networkTab, items.alerts],
  persona: PERSONAS.ops,
  organizationName: (_data, locale) => localize(BRAND_NAME, locale),
  alertsPath: items.alerts.path,
  openAlertCount: (data) => data.alerts.filter((alert) => alert.status === 'open').length,
};

export const OPS_NETWORK_PATHS = { stations: items.stations.path, devices: items.devices.path, sync: items.sync.path } as const;
