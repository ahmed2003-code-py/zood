import '@/public/i18n';
import '@/public/stage.css';
import { FileText, LogIn, ShieldCheck } from 'lucide-react';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Banner, BrandLogo, Icon } from '@/design-system';
import ar from '@/i18n/locales/ar/legal.json';
import en from '@/i18n/locales/en/legal.json';
import { useLocale } from '@/i18n/useLocale';
import { formatDate } from '@/lib/format';
import { mailtoUrl } from '@/landing/contact';
import { publicControlClass } from '@/public/controls';
import { registerNamespace } from '@/public/i18n';
import { useThemeCrossfade } from '@/public/motion';
import { LanguageToggle, ThemeToggle } from '@/public/PreferenceButtons';
import { StageAurora } from '@/public/StageAurora';

registerNamespace('legal', { ar, en });

/** When the texts below were last changed; shown at the top of both pages. */
const LEGAL_UPDATED = '2026-11-21';

type Kind = 'privacy' | 'terms';
type Sections = Record<string, { title: string; body: string[] }>;

/** Placeholders the owner still has to fill in are written in [brackets] and shown highlighted. */
function withPlaceholders(text: string) {
  return text.split(/(\[[^\]]+\])/g).map((part, index) =>
    part.startsWith('[') ? (
      <mark key={index} className="rounded-sm bg-warning-subtle px-1 text-warning">
        {part}
      </mark>
    ) : (
      part
    ),
  );
}

/** `/privacy` and `/terms`: plain, honest, in both languages. Lazy, like every public page. */
function LegalPage({ kind }: { kind: Kind }) {
  const { t } = useTranslation('legal');
  const { t: tSite } = useTranslation('site');
  const { locale } = useLocale();
  useThemeCrossfade();
  const sections = t(`${kind}.sections`, { returnObjects: true }) as unknown as Sections;
  const other: Kind = kind === 'privacy' ? 'terms' : 'privacy';

  useEffect(() => {
    const previous = document.title;
    document.title = `${t(`${kind}.title`)} | ZAWED`;
    return () => {
      document.title = previous;
    };
  }, [t, kind]);

  return (
    <div className="public-page min-h-dvh bg-background">
      <header className="relative isolate overflow-hidden bg-stage text-stage-foreground">
        <StageAurora />
        <div className="mx-auto flex h-16 w-full max-w-6xl items-center gap-1 px-4 md:px-6">
          <Link to="/" aria-label={tSite('a11y.home')} className="me-auto flex min-h-10 items-center rounded-control">
            <BrandLogo size="sm" tone="inverse" />
          </Link>
          <LanguageToggle tone="stage" />
          <ThemeToggle tone="stage" />
          <Link to="/login" className={publicControlClass('stage', 'px-3 max-sm:w-10 max-sm:px-0')}>
            <Icon icon={LogIn} size={17} directional />
            <span className="max-sm:sr-only">{tSite('links.signIn')}</span>
          </Link>
        </div>
        <div className="mx-auto w-full max-w-3xl px-4 pt-10 pb-14 md:px-6 md:pt-14 md:pb-16">
          <h1 className="public-display text-3xl leading-tight font-bold text-balance md:text-[2.75rem]">{t(`${kind}.title`)}</h1>
          <p className="mt-4 max-w-2xl text-lg leading-relaxed text-pretty text-stage-muted">{t(`${kind}.lead`)}</p>
          <p className="mt-5 text-sm text-stage-muted">{t('updated', { date: formatDate(LEGAL_UPDATED, locale) })}</p>
        </div>
      </header>

      <main id="main" className="mx-auto grid w-full max-w-6xl gap-10 px-4 py-10 md:px-6 md:py-14 lg:grid-cols-[14rem_minmax(0,1fr)] lg:gap-14">
        <nav aria-labelledby="legal-contents" className="lg:sticky lg:top-8 lg:self-start">
          <h2 id="legal-contents" className="text-sm font-semibold">
            {t('contents')}
          </h2>
          <ol className="mt-2 flex flex-col">
            {Object.entries(sections).map(([id, section]) => (
              <li key={id}>
                <a href={`#${id}`} className="public-link inline-flex min-h-9 items-center text-sm text-muted hover:text-foreground">
                  {section.title}
                </a>
              </li>
            ))}
          </ol>
        </nav>

        <article className="min-w-0 max-w-3xl">
          <Banner tone="warning" title={t('draft.title')} description={t('draft.body')} />
          {Object.entries(sections).map(([id, section]) => (
            <section key={id} id={id} aria-labelledby={`${id}-title`} className="scroll-mt-6 border-b border-border py-8 last:border-b-0">
              <h2 id={`${id}-title`} className="text-xl font-semibold">
                {section.title}
              </h2>
              <div className="mt-3 flex flex-col gap-3 text-[0.9375rem] leading-relaxed text-pretty text-muted">
                {section.body.map((paragraph, index) => (
                  <p key={index}>{withPlaceholders(paragraph)}</p>
                ))}
              </div>
            </section>
          ))}
          <Link
            to={`/${other}`}
            className="mt-4 inline-flex min-h-11 items-center gap-2 rounded-control border border-border-strong px-4 text-sm font-semibold transition-colors hover:bg-surface-sunken"
          >
            <Icon icon={other === 'privacy' ? ShieldCheck : FileText} size={17} />
            {t(`other.${other}`)}
          </Link>
        </article>
      </main>

      <footer className="border-t border-border">
        <nav aria-label={tSite('links.legal')} className="mx-auto flex w-full max-w-6xl flex-wrap gap-x-6 gap-y-1 px-4 py-6 text-sm text-muted md:px-6">
          <Link to="/" className="public-link inline-flex min-h-10 items-center hover:text-foreground">
            {tSite('links.backToSite')}
          </Link>
          <Link to="/privacy" className="public-link inline-flex min-h-10 items-center hover:text-foreground">
            {tSite('links.privacy')}
          </Link>
          <Link to="/terms" className="public-link inline-flex min-h-10 items-center hover:text-foreground">
            {tSite('links.terms')}
          </Link>
          <a href={mailtoUrl()} className="public-link inline-flex min-h-10 items-center hover:text-foreground">
            {tSite('links.contact')}
          </a>
        </nav>
      </footer>
    </div>
  );
}

export function PrivacyPage() {
  return <LegalPage kind="privacy" />;
}

export function TermsPage() {
  return <LegalPage kind="terms" />;
}
