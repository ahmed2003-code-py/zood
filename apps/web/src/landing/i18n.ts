/**
 * The landing page's copy lives in its own namespace and is registered when the landing chunk
 * loads, so the portals' entry chunk does not carry it. i18n.parity.test.ts checks both languages.
 */
import { i18n } from '@/i18n';
import ar from '@/i18n/locales/ar/landing.json';
import en from '@/i18n/locales/en/landing.json';

export const LANDING_NAMESPACE = 'landing';

for (const [locale, bundle] of [
  ['ar', ar],
  ['en', en],
] as const) {
  if (!i18n.hasResourceBundle(locale, LANDING_NAMESPACE)) i18n.addResourceBundle(locale, LANDING_NAMESPACE, bundle);
}
