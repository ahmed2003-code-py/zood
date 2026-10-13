export const LOCALES = ['ar', 'en'] as const;
export type Locale = (typeof LOCALES)[number];
export type Direction = 'rtl' | 'ltr';

export const DEFAULT_LOCALE: Locale = 'ar';

export const LOCALE_DIRECTION: Record<Locale, Direction> = { ar: 'rtl', en: 'ltr' };

/** Language names shown in the language switcher, always in their own language. */
export const LOCALE_ENDONYMS: Record<Locale, string> = { ar: 'العربية', en: 'English' };

/** Display text stored in both languages (entity names such as stations, fuels, companies). */
export type LocalizedText = Record<Locale, string>;

/** The text of an entity name in the current language. */
export function localize(text: LocalizedText | undefined, locale: Locale): string {
  return text?.[locale] ?? '';
}

export function isLocale(value: unknown): value is Locale {
  return typeof value === 'string' && (LOCALES as readonly string[]).includes(value);
}
