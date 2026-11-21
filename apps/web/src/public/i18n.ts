/**
 * Copy shared by the public pages (sign-in frame and panel, landing header, legal pages). It is
 * registered when a public chunk loads, so the portals' entry chunk does not carry it.
 * i18n.parity.test.ts checks both languages.
 */
import { i18n } from '@/i18n';
import ar from '@/i18n/locales/ar/site.json';
import en from '@/i18n/locales/en/site.json';

export const SITE_NAMESPACE = 'site';

/** Adds a lazily loaded namespace in both languages, once. */
export function registerNamespace(namespace: string, bundles: { ar: object; en: object }): void {
  for (const locale of ['ar', 'en'] as const) {
    if (!i18n.hasResourceBundle(locale, namespace)) i18n.addResourceBundle(locale, namespace, bundles[locale]);
  }
}

registerNamespace(SITE_NAMESPACE, { ar, en });
