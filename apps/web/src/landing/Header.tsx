import { LogIn, Menu, Moon, Sun, X } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { BrandLogo, Icon } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { LOCALE_ENDONYMS } from '@/lib/locale';
import { useTheme } from '@/theme/theme-context';
import { SECTION_LINKS } from './section-links';

/**
 * Fixed header. Over the green hero it is transparent with light text; once the page scrolls it
 * turns into a solid bar in the page theme. On phones the section links fold into a menu.
 */
export function LandingHeader() {
  const { t } = useTranslation('landing');
  const { locale, setLocale } = useLocale();
  const { resolved, setPreference } = useTheme();
  const [elevated, setElevated] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const solid = elevated || menuOpen;
  const other = locale === 'ar' ? 'en' : 'ar';

  useEffect(() => {
    const onScroll = () => setElevated(window.scrollY > 24);
    onScroll();
    window.addEventListener('scroll', onScroll, { passive: true });
    return () => window.removeEventListener('scroll', onScroll);
  }, []);

  const control = cn(
    'inline-flex min-h-10 items-center justify-center gap-2 rounded-control px-3 text-sm font-medium transition-colors',
    solid ? 'text-foreground hover:bg-surface-sunken' : 'text-hero-foreground hover:bg-hero-panel',
  );

  return (
    <header
      className={cn(
        'fixed inset-x-0 top-0 z-30 border-b transition-[background-color,border-color] duration-300',
        solid ? 'border-border bg-background/92 backdrop-blur-md' : 'border-transparent bg-transparent',
      )}
    >
      <a
        href="#main"
        className="sr-only rounded-control bg-surface px-3 py-2 text-sm font-medium text-foreground focus:not-sr-only focus:absolute focus:start-3 focus:top-3"
      >
        {t('a11y.skip')}
      </a>
      <div className="mx-auto flex h-16 max-w-6xl items-center gap-2 px-4 md:px-6">
        <a href="#top" aria-label={t('a11y.home')} className="me-auto flex items-center rounded-control lg:me-6">
          <BrandLogo size="sm" tone={solid ? 'default' : 'inverse'} />
        </a>

        <nav aria-label={t('a11y.primaryNav')} className="me-auto hidden lg:block">
          <ul className="flex items-center gap-1">
            {SECTION_LINKS.map((id) => (
              <li key={id}>
                <a href={`#${id}`} className={control}>
                  {t(`nav.${id}`)}
                </a>
              </li>
            ))}
          </ul>
        </nav>

        <button type="button" lang={other} aria-label={t('a11y.language')} className={cn(control, 'px-2.5')} onClick={() => setLocale(other)}>
          {LOCALE_ENDONYMS[other]}
        </button>
        <button
          type="button"
          aria-label={t(resolved === 'dark' ? 'a11y.themeToLight' : 'a11y.themeToDark')}
          className={cn(control, 'w-10 px-0')}
          onClick={() => setPreference(resolved === 'dark' ? 'light' : 'dark')}
        >
          <Icon icon={resolved === 'dark' ? Sun : Moon} size={18} />
        </button>
        <Link to="/login" className={cn(control, 'hidden sm:inline-flex')}>
          <Icon icon={LogIn} size={17} directional />
          {t('nav.signIn')}
        </Link>
        <a
          href="#demo"
          className="hidden min-h-10 items-center rounded-control bg-accent px-4 text-sm font-semibold text-accent-foreground transition-[filter] hover:brightness-105 sm:inline-flex"
        >
          {t('nav.requestDemo')}
        </a>
        <button
          type="button"
          aria-expanded={menuOpen}
          aria-controls="landing-menu"
          aria-label={t(menuOpen ? 'a11y.closeMenu' : 'a11y.menu')}
          className={cn(control, 'w-10 px-0 lg:hidden')}
          onClick={() => setMenuOpen((open) => !open)}
        >
          <Icon icon={menuOpen ? X : Menu} size={20} />
        </button>
      </div>

      {menuOpen && (
        <nav id="landing-menu" aria-label={t('a11y.primaryNav')} className="border-t border-border bg-background lg:hidden">
          <ul className="mx-auto flex max-w-6xl flex-col px-4 py-2 md:px-6">
            {SECTION_LINKS.map((id) => (
              <li key={id}>
                <a href={`#${id}`} onClick={() => setMenuOpen(false)} className="flex min-h-12 items-center border-b border-border text-base font-medium">
                  {t(`nav.${id}`)}
                </a>
              </li>
            ))}
            <li className="flex gap-2 py-3">
              <Link
                to="/login"
                className="inline-flex min-h-11 flex-1 items-center justify-center gap-2 rounded-control border border-border-strong text-sm font-semibold"
              >
                <Icon icon={LogIn} size={17} directional />
                {t('nav.signIn')}
              </Link>
              <a
                href="#demo"
                onClick={() => setMenuOpen(false)}
                className="inline-flex min-h-11 flex-1 items-center justify-center rounded-control bg-accent text-sm font-semibold text-accent-foreground"
              >
                {t('nav.requestDemo')}
              </a>
            </li>
          </ul>
        </nav>
      )}
    </header>
  );
}
