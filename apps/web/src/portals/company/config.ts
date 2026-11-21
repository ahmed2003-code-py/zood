import { Bell, ChartColumn, Gauge, House, LifeBuoy, MessageSquareText, PiggyBank, Receipt, Settings, Truck, Wallet } from 'lucide-react';
import { API_MODE } from '@/api';
import { CalendarDays } from 'lucide-react';
import { localize } from '@/lib/locale';
import { DEMO_CONTEXT, PERSONAS } from '@/mocks/personas';
import { openAlerts } from '@/mocks/selectors';
import type { NavItem, PortalConfig } from '@/shell/types';

const base = '/company';

const items = {
  home: { id: 'home', path: base, icon: House },
  vehicles: { id: 'vehicles', path: `${base}/vehicles`, icon: Truck },
  transactions: { id: 'transactions', path: `${base}/transactions`, icon: Receipt },
  calendar: { id: 'calendar', path: `${base}/activity`, icon: CalendarDays },
  wallet: { id: 'wallet', path: `${base}/wallet`, icon: Wallet },
  budgets: { id: 'budgets', path: `${base}/budgets`, icon: PiggyBank },
  limits: { id: 'limits', path: `${base}/limits`, icon: Gauge },
  alerts: { id: 'alerts', path: `${base}/alerts`, icon: Bell },
  reports: { id: 'reports', path: `${base}/reports`, icon: ChartColumn },
  requests: { id: 'requests', path: `${base}/requests`, icon: MessageSquareText },
  tickets: { id: 'tickets', path: `${base}/tickets`, icon: LifeBuoy },
  settings: { id: 'settings', path: `${base}/settings`, icon: Settings },
} satisfies Record<string, NavItem>;

export const companyPortal: PortalConfig = {
  id: 'company',
  basePath: base,
  groups: [
    { id: 'main', items: [items.home] },
    { id: 'fleet', label: 'fleet', items: [items.vehicles] },
    { id: 'fuel', label: 'fuel', items: [items.transactions, ...(API_MODE === 'http' ? [items.calendar] : []), items.wallet, items.budgets, items.limits] },
    { id: 'insights', items: [items.alerts, items.reports] },
    { id: 'support', label: 'support', items: [API_MODE === 'http' ? items.tickets : items.requests] },
    { id: 'settings', items: [items.settings] },
  ],
  // Alerts reach phones through the bell and the home page; the wallet is what people come back for.
  mobileTabs: [items.home, items.transactions, items.vehicles, items.wallet],
  persona: PERSONAS.company,
  organizationName: (data, locale) => localize(data.companies[DEMO_CONTEXT.companyId]?.name, locale),
  alertsPath: items.alerts.path,
  openAlertCount: (data) => openAlerts(data, { companyId: DEMO_CONTEXT.companyId }).length,
};
