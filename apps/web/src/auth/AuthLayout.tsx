import '@/public/stage.css';
import './stage/signin.css';
import { useEffect, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { BrandLogo } from '@/design-system';
import { mailtoUrl } from '@/landing/contact';
import { useThemeCrossfade } from '@/public/motion';
import { LanguageToggle, ThemeToggle } from '@/public/PreferenceButtons';
import FuelBackdrop from './stage/FuelBackdrop';
import { FuelDrop } from './stage/FuelDrop';
import type { SignInState } from './stage/state';


interface AuthLayoutProps {
  title: string;
  description?: string;
  children: ReactNode;
  /** Shown under the card, small (the sign-in page's "who is this for"). */
  footnote?: ReactNode;
  /** The moment the screen is in; the drop, the card and the backdrop follow it. */
  state?: SignInState;
  /** Changes on every refusal ('a' ↔ 'b'), so the card shakes again each time. */
  shake?: 'a' | 'b';
  /** Signed in: the card shows this welcome instead of the form while the portal opens. */
  welcome?: { title: string; detail: string };
}

/**
 * The frame of every sign-in screen (sign-in, second factor, set-up, password change, wrong portal):
 * one centred card on a full-screen backdrop made of ZAWED's own marks in motion. The card's fuel
 * drop fills while a request runs, fills to the top with a check when the sign-in succeeds, and
 * drains when it is refused.
 */
export function AuthLayout({ title, description, children, footnote, state = 'idle', shake, welcome }: AuthLayoutProps) {
  const { t } = useTranslation('site');
  useThemeCrossfade();
  const success = state === 'success' && welcome;
  // The backdrop is decoration: it mounts after the card's first paint. It ships in this route's
  // chunk; a nested dynamic import here made the bundler fold the design system into the entry.
  const [decorated, setDecorated] = useState(false);
  useEffect(() => {
    const frame = requestAnimationFrame(() => setDecorated(true));
    return () => cancelAnimationFrame(frame);
  }, []);

  return (
    <div className="signin-page public-page relative isolate flex min-h-dvh flex-col bg-background" data-state={state}>
      {decorated && <FuelBackdrop />}

      <header className="relative z-10 flex h-16 items-center justify-between gap-2 px-3 sm:px-6">
        <Link to="/" aria-label={t('a11y.home')} className="flex min-h-10 items-center rounded-control px-1">
          <BrandLogo size="sm" />
        </Link>
        <div className="flex items-center gap-1">
          <LanguageToggle tone="page" />
          <ThemeToggle tone="page" />
        </div>
      </header>

      <main className="relative z-10 flex flex-1 flex-col items-center justify-center px-4 pt-10 pb-8 sm:px-6">
        <section aria-labelledby="auth-title" className="signin-card relative w-full max-w-[27rem]" data-shake={shake}>
          <div className="relative rounded-dialog border border-border bg-surface-elevated px-5 pt-14 pb-7 shadow-dialog sm:px-8 sm:pb-8">
            {/* The emblem sits on the card's top edge. */}
            <div className="absolute inset-x-0 top-0 flex -translate-y-1/2 justify-center">
              <span className="flex h-[4.5rem] items-center rounded-full border border-border bg-surface-elevated px-5 shadow-elevated">
                <FuelDrop state={state} />
              </span>
            </div>

            {success ? (
              <div role="status" className="signin-welcome flex flex-col items-center py-4 text-center">
                <h1 id="auth-title" className="public-display text-2xl leading-tight font-semibold text-balance">
                  {welcome.title}
                </h1>
                <p className="mt-2 text-[0.9375rem] text-muted">{welcome.detail}</p>
              </div>
            ) : (
              <>
                <div className="text-center">
                  <h1 id="auth-title" className="public-display text-2xl leading-tight font-semibold text-balance">
                    {title}
                  </h1>
                  {description && <p className="mt-2 text-[0.9375rem] leading-relaxed text-pretty text-muted">{description}</p>}
                </div>
                <div className="mt-7">{children}</div>
              </>
            )}
          </div>
          {footnote && !success && <div className="mx-auto mt-5 max-w-sm px-2 text-center">{footnote}</div>}
        </section>
      </main>

      <footer className="relative z-10 px-4 pb-5 sm:px-6">
        <nav aria-label={t('links.legal')} className="flex flex-wrap items-center justify-center gap-x-5 gap-y-1 text-xs text-muted">
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
