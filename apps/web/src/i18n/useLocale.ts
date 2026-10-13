import { useTranslation } from 'react-i18next';
import { DEFAULT_LOCALE, isLocale, LOCALE_DIRECTION, type Direction, type Locale } from '@/lib/locale';
import { changeLocale } from './index';

export interface LocaleState {
  locale: Locale;
  dir: Direction;
  setLocale: (locale: Locale) => void;
}

export function useLocale(): LocaleState {
  const { i18n } = useTranslation();
  const current = i18n.resolvedLanguage ?? i18n.language;
  const locale = isLocale(current) ? current : DEFAULT_LOCALE;
  return { locale, dir: LOCALE_DIRECTION[locale], setLocale: changeLocale };
}
