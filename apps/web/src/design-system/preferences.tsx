import { Monitor, Moon, Sun } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { LOCALE_ENDONYMS, LOCALES, type Locale } from '@/lib/locale';
import { useTheme, type ThemePreference } from '@/theme/theme-context';
import { SegmentedControl, type SegmentOption } from './forms';

const LANGUAGE_OPTIONS: SegmentOption<Locale>[] = LOCALES.map((locale) => ({
  value: locale,
  label: LOCALE_ENDONYMS[locale],
  lang: locale,
}));

/** "العربية | English". Applies immediately and is remembered on this device. */
export function LanguageSwitcher({ className }: { className?: string }) {
  const { t } = useTranslation('settings');
  const { locale, setLocale } = useLocale();
  return (
    <SegmentedControl
      label={t('display.language')}
      value={locale}
      onValueChange={setLocale}
      options={LANGUAGE_OPTIONS}
      className={className}
    />
  );
}

export function ThemeSwitcher({ className }: { className?: string }) {
  const { t } = useTranslation('settings');
  const { preference, setPreference } = useTheme();
  const options: SegmentOption<ThemePreference>[] = [
    { value: 'light', label: t('display.theme.light'), icon: Sun },
    { value: 'dark', label: t('display.theme.dark'), icon: Moon },
    { value: 'system', label: t('display.theme.system'), icon: Monitor },
  ];
  return (
    <SegmentedControl
      label={t('display.appearance')}
      value={preference}
      onValueChange={setPreference}
      options={options}
      className={className}
    />
  );
}

/** Settings section reused by every portal's settings, the POS agent menu and the presenter panel. */
export function AppearanceSettings({ className }: { className?: string }) {
  const { t } = useTranslation('settings');
  return (
    <section className={cn('rounded-container border border-border bg-surface', className)}>
      <header className="border-b border-border px-4 py-3">
        <h2 className="text-base font-semibold">{t('display.title')}</h2>
        <p className="text-sm text-muted">{t('display.description')}</p>
      </header>
      <div className="divide-y divide-border">
        <div className="flex flex-col gap-2 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
          <span className="text-sm font-medium">{t('display.language')}</span>
          <LanguageSwitcher className="w-full sm:w-auto" />
        </div>
        <div className="flex flex-col gap-2 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
          <span className="text-sm font-medium">{t('display.appearance')}</span>
          <ThemeSwitcher className="w-full sm:w-auto" />
        </div>
      </div>
    </section>
  );
}
