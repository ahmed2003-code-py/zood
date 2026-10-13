import { Bell, ChartColumn, Gauge, House, MessageSquareText, PiggyBank, Receipt, Settings, Truck, Wallet } from 'lucide-react';
import { localize } from '@/lib/locale';
import { DEMO_CONTEXT, PERSONAS } from '@/mocks/personas';
import { openAlerts } from '@/mocks/selectors';
import type { NavItem, PortalConfig } from '@/shell/types';

const base = '/company';

const items = {
  home: { id: 'home', path: base, icon: House },
  vehicles: { id: 'vehicles', path: `${base}/vehicles`, icon: Truck },
  transactions: { id: 'transactions', path: `${base}/transactions`, icon: Receipt },
  wallet: { id: 'wallet', path: `${base}/wallet`, icon: Wallet },
  budgets: { id: 'budgets', path: `${base}/budgets`, icon: PiggyBank },
  limits: { id: 'limits', path: `${base}/limits`, icon: Gauge },
  alerts: { id: 'alerts', path: `${base}/alerts`, icon: Bell },
  reports: { id: 'reports', path: `${base}/reports`, icon: ChartColumn },
  requests: { id: 'requests', path: `${base}/requests`, icon: MessageSquareText },
  settings: { id: 'settings', path: `${base}/settings`, icon: Settings },
} satisfies Record<string, NavItem>;

export const companyPortal: PortalConfig = {
  id: 'company',
  basePath: base,
  groups: [
    { id: 'main', items: [items.home] },
    { id: 'fleet', label: 'fleet', items: [items.vehicles] },
    { id: 'fuel', label: 'fuel', items: [items.transactions, items.wallet, items.budgets, items.limits] },
    { id: 'insights', items: [items.alerts, items.reports] },
    { id: 'support', label: 'support', items: [items.requests] },
    { id: 'settings', items: [items.settings] },
  ],
  mobileTabs: [items.home, items.transactions, items.vehicles, items.alerts],
  persona: PERSONAS.company,
  organizationName: (data, locale) => localize(data.companies[DEMO_CONTEXT.companyId]?.name, locale),
  alertsPath: items.alerts.path,
  openAlertCount: (data) => openAlerts(data, { companyId: DEMO_CONTEXT.companyId }).length,
};
