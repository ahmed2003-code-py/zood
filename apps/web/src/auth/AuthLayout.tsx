import { lazy, Suspense, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { BrandLogo } from '@/design-system';
import { mailtoUrl } from '@/landing/contact';
import '@/public/stage.css';
import { useThemeCrossfade } from '@/public/motion';
import { LanguageToggle, ThemeToggle } from '@/public/PreferenceButtons';
import { PANEL_INNER, PANEL_OUTER } from './brand/panel-frame';

// The expressive half loads on its own, so the form is usable before it arrives.
const BrandPanel = lazy(() => import('./brand/BrandPanel'));

/**
 * The frame of every sign-in screen (sign-in, second factor, password change, wrong portal).
 *
 * From lg it is two columns: the form at the inline start, calm and focused, and the brand panel
 * at the inline end, which tells the visitor what ZAWED is. On phones the panel folds into a short
 * animated band and the form rises over it like a sheet. The DOM order is header, form, footer,
 * panel, so keyboard and screen-reader users reach the form first.
 */
export function AuthLayout({
  title,
  description,
  children,
  footnote,
}: {
  title: string;
  description?: string;
  children: ReactNode;
  /** Shown under the form after a hairline (the sign-in page's "who is this for"). */
  footnote?: ReactNode;
}) {
  const { t } = useTranslation('site');
  useThemeCrossfade();

  return (
    <div className="public-page grid min-h-dvh grid-cols-1 grid-rows-[auto_auto_1fr_auto] bg-background lg:grid-cols-[minmax(0,1fr)_minmax(0,1.05fr)] lg:grid-rows-[auto_1fr_auto]">
      <header className="relative z-10 col-start-1 row-start-1 flex h-16 items-center justify-between gap-2 px-3 sm:px-5 lg:h-auto lg:px-10 lg:pt-7 lg:pb-2 xl:px-14">
        <Link to="/" aria-label={t('a11y.home')} className="flex min-h-10 items-center rounded-control px-1">
          <BrandLogo size="sm" tone="inverse" className="lg:hidden" />
          <BrandLogo size="sm" className="hidden lg:block" />
        </Link>
        <div className="flex items-center gap-1">
          <LanguageToggle tone="auto" />
          <ThemeToggle tone="auto" />
        </div>
      </header>

      <main className="relative z-10 col-start-1 row-start-3 -mt-6 flex justify-center rounded-t-[1.5rem] bg-background px-4 pt-8 pb-10 sm:px-6 lg:row-start-2 lg:mt-0 lg:items-center lg:rounded-none lg:px-10 lg:py-10 xl:px-14">
        <section aria-labelledby="auth-title" className="w-full max-w-md animate-[zood-dialog-in_420ms_cubic-bezier(0.22,1,0.36,1)] lg:max-w-[25rem]">
          <h1 id="auth-title" className="public-display text-2xl leading-tight font-semibold text-balance sm:text-[1.75rem]">
            {title}
          </h1>
          {description && <p className="mt-2 text-[0.9375rem] leading-relaxed text-pretty text-muted">{description}</p>}
          <div className="mt-7">{children}</div>
          {footnote && <div className="mt-8 border-t border-border pt-6">{footnote}</div>}
        </section>
      </main>

      <footer className="col-start-1 row-start-4 px-4 pb-6 sm:px-6 lg:row-start-3 lg:px-10 lg:pb-7 xl:px-14">
        <nav aria-label={t('links.legal')} className="mx-auto flex max-w-md flex-wrap items-center justify-center gap-x-5 gap-y-1 text-xs text-muted lg:mx-0 lg:max-w-none lg:justify-start">
          <Link to="/" className="public-link inline-flex min-h-8 items-center hover:text-foreground">
            {t('links.backToSite')}
          </Link>
          <Link to="/privacy" className="public-link inline-flex min-h-8 items-center hover:text-foreground">
            {t('links.privacy')}
          </Link>
          <Link to="/terms" className="public-link inline-flex min-h-8 items-center hover:text-foreground">
            {t('links.terms')}
          </Link>
          <a href={mailtoUrl()} className="public-link inline-flex min-h-8 items-center hover:text-foreground">
            {t('links.contact')}
          </a>
        </nav>
      </footer>

      <aside aria-label={t('panel.title')} className={PANEL_OUTER}>
        <Suspense fallback={<div className={PANEL_INNER} />}>
          <BrandPanel />
        </Suspense>
      </aside>
    </div>
  );
}

/** A refusal shown above a form; announced to screen readers. */
export function FormError({ message, reference }: { message?: string; reference?: string }) {
  if (!message) return null;
  return (
    <div role="alert" className="rounded-control border border-danger bg-danger-subtle px-3 py-2.5 text-sm text-danger">
      <p className="font-medium">{message}</p>
      {reference && <p className="mt-0.5 text-xs">{reference}</p>}
    </div>
  );
}
