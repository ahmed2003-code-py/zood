/**
 * The portals' newer copy (home dashboards, budgets, limits, reports, notifications, audit names)
 * lives in its own namespace, registered when a portal chunk loads, so the entry chunk that the
 * landing page and sign-in share does not carry it. i18n.parity.test.ts checks both languages.
 */
import { i18n } from '@/i18n';
import ar from '@/i18n/locales/ar/portal.json';
import en from '@/i18n/locales/en/portal.json';

export const PORTAL_NAMESPACE = 'portal';

for (const [locale, bundle] of [
  ['ar', ar],
  ['en', en],
] as const) {
  if (!i18n.hasResourceBundle(locale, PORTAL_NAMESPACE)) i18n.addResourceBundle(locale, PORTAL_NAMESPACE, bundle);
}
