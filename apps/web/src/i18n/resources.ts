import arAlerts from './locales/ar/alerts.json';
import arCommon from './locales/ar/common.json';
import arCompany from './locales/ar/company.json';
import arDemo from './locales/ar/demo.json';
import arDev from './locales/ar/dev.json';
import arErrors from './locales/ar/errors.json';
import arNavigation from './locales/ar/navigation.json';
import arOps from './locales/ar/ops.json';
import arPos from './locales/ar/pos.json';
import arPresenter from './locales/ar/presenter.json';
import arReceipt from './locales/ar/receipt.json';
import arSettings from './locales/ar/settings.json';
import arShell from './locales/ar/shell.json';
import arStation from './locales/ar/station.json';
import arStatus from './locales/ar/status.json';
import arTransactions from './locales/ar/transactions.json';
import enAlerts from './locales/en/alerts.json';
import enCommon from './locales/en/common.json';
import enCompany from './locales/en/company.json';
import enDemo from './locales/en/demo.json';
import enDev from './locales/en/dev.json';
import enErrors from './locales/en/errors.json';
import enNavigation from './locales/en/navigation.json';
import enOps from './locales/en/ops.json';
import enPos from './locales/en/pos.json';
import enPresenter from './locales/en/presenter.json';
import enReceipt from './locales/en/receipt.json';
import enSettings from './locales/en/settings.json';
import enShell from './locales/en/shell.json';
import enStation from './locales/en/station.json';
import enStatus from './locales/en/status.json';
import enTransactions from './locales/en/transactions.json';

/**
 * Translation resources, one namespace per domain. Each feature phase adds its own namespace
 * in BOTH languages in the same change; i18n.parity.test.ts fails if the key sets diverge.
 */
export const resources = {
  ar: {
    common: arCommon,
    status: arStatus,
    settings: arSettings,
    errors: arErrors,
    dev: arDev,
    navigation: arNavigation,
    shell: arShell,
    alerts: arAlerts,
    company: arCompany,
    station: arStation,
    ops: arOps,
    pos: arPos,
    demo: arDemo,
    presenter: arPresenter,
    transactions: arTransactions,
    receipt: arReceipt,
  },
  en: {
    common: enCommon,
    status: enStatus,
    settings: enSettings,
    errors: enErrors,
    dev: enDev,
    navigation: enNavigation,
    shell: enShell,
    alerts: enAlerts,
    company: enCompany,
    station: enStation,
    ops: enOps,
    pos: enPos,
    demo: enDemo,
    presenter: enPresenter,
    transactions: enTransactions,
    receipt: enReceipt,
  },
} as const;

export const NAMESPACES = Object.keys(resources.en) as Array<keyof (typeof resources)['en']>;
export const DEFAULT_NAMESPACE = 'common';
