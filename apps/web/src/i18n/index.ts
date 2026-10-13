import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import { DEFAULT_LOCALE, isLocale, LOCALE_DIRECTION, type Locale } from '@/lib/locale';
import { readStorage, readUrlParam, STORAGE_KEYS, writeStorage } from '@/lib/storage';
import { DEFAULT_NAMESPACE, NAMESPACES, resources } from './resources';

/**
 * Initial language: ?lang= (non-persisted override, used by previews) > saved preference > Arabic.
 * Production: a per-user preference from the profile will take precedence over the device value.
 */
const urlLocale = readUrlParam('lang');
const localeOverridden = isLocale(urlLocale);
const storedLocale = readStorage(STORAGE_KEYS.locale);
const initialLocale: Locale = localeOverridden ? urlLocale : isLocale(storedLocale) ? storedLocale : DEFAULT_LOCALE;

export function applyDocumentLocale(locale: Locale): void {
  const root = document.documentElement;
  root.lang = locale;
  root.dir = LOCALE_DIRECTION[locale];
}

void i18n.use(initReactI18next).init({
  resources,
  lng: initialLocale,
  fallbackLng: DEFAULT_LOCALE,
  ns: NAMESPACES,
  defaultNS: DEFAULT_NAMESPACE,
  interpolation: { escapeValue: false },
  returnNull: false,
  initAsync: false,
});

applyDocumentLocale(initialLocale);
i18n.on('languageChanged', (lng) => {
  if (isLocale(lng)) applyDocumentLocale(lng);
});

/** Changes the UI language immediately (no reload) and remembers it unless it was set via ?lang=. */
export function changeLocale(locale: Locale): void {
  void i18n.changeLanguage(locale);
  if (!localeOverridden) writeStorage(STORAGE_KEYS.locale, locale);
}

// Keep other open windows of the demo (portal, POS, presenter) on the same language.
if (!localeOverridden) {
  window.addEventListener('storage', (event) => {
    if (event.key === STORAGE_KEYS.locale && isLocale(event.newValue) && event.newValue !== i18n.language) {
      void i18n.changeLanguage(event.newValue);
    }
  });
}

export { i18n };
