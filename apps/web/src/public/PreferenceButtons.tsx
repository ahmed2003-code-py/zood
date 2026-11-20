import './i18n';
import { Moon, Sun } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Icon } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { publicControlClass, type PublicTone } from './controls';
import { LOCALE_ENDONYMS } from '@/lib/locale';
import { useTheme } from '@/theme/theme-context';

/**
 * Switches to the other language. The visible text is the other language's own name, and the
 * accessible name starts with "Language:" and then repeats that text (WCAG 2.5.3, label in name).
 */
export function LanguageToggle({ tone, className }: { tone: PublicTone; className?: string }) {
  const { t } = useTranslation('site');
  const { locale, setLocale } = useLocale();
  const other = locale === 'ar' ? 'en' : 'ar';
  return (
    <button type="button" className={publicControlClass(tone, cn('px-2.5', className))} onClick={() => setLocale(other)}>
      <span className="sr-only">{t('a11y.language')} </span>
      <span lang={other}>{LOCALE_ENDONYMS[other]}</span>
    </button>
  );
}

/** Light ↔ Dark in one press; the page cross-fades (useThemeCrossfade). */
export function ThemeToggle({ tone, className }: { tone: PublicTone; className?: string }) {
  const { t } = useTranslation('site');
  const { resolved, setPreference } = useTheme();
  const dark = resolved === 'dark';
  return (
    <button
      type="button"
      aria-label={t(dark ? 'a11y.toLight' : 'a11y.toDark')}
      className={publicControlClass(tone, cn('w-10 px-0 pointer-coarse:w-11', className))}
      onClick={() => setPreference(dark ? 'light' : 'dark')}
    >
      <Icon icon={dark ? Sun : Moon} size={18} />
    </button>
  );
}
