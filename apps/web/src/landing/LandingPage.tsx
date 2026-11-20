import './i18n';
import './landing.css';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { BrandLogo, LanguageSwitcher, ThemeSwitcher } from '@/design-system';
import type { FleetSize } from '@/domain/demo-request';
import { CONTACT, mailtoUrl } from './contact';
import { DemoForm, DirectContact } from './DemoForm';
import { Faq } from './Faq';
import { LandingHeader } from './Header';
import { usePrefersReducedMotion, useScrollReveal } from './hooks';
import { Pricing } from './Pricing';
import { SECTION_LINKS } from './section-links';
import { Fleets, Hero, HowItWorks, MadeInEgypt, Problem, Stations, Trust } from './sections';

/** `/`: the public landing page, in both API modes. Lazy-loaded; the portals never download it. */
export function LandingPage() {
  const { t, i18n } = useTranslation('landing');
  const root = useRef<HTMLDivElement>(null);
  const reducedMotion = usePrefersReducedMotion();
  const [fleetSize, setFleetSize] = useState<FleetSize | ''>('');
  useScrollReveal(root, !reducedMotion);

  // Title and description follow the language.
  useEffect(() => {
    const previousTitle = document.title;
    document.title = t('meta.title');
    let meta = document.querySelector<HTMLMetaElement>('meta[name="description"]');
    const created = !meta;
    if (!meta) {
      meta = document.createElement('meta');
      meta.name = 'description';
      document.head.append(meta);
    }
    meta.content = t('meta.description');
    return () => {
      document.title = previousTitle;
      if (created) meta.remove();
    };
  }, [t, i18n.language]);

  /** A pricing tier's "Get a quote": pick its fleet size in the form and take the visitor there. */
  const chooseTier = (size: FleetSize) => {
    setFleetSize(size);
    document.getElementById('demo')?.scrollIntoView({ behavior: reducedMotion ? 'auto' : 'smooth', block: 'start' });
    document.querySelector<HTMLInputElement>('#demo input[name="companyName"]')?.focus({ preventScroll: true });
  };

  return (
    <div ref={root} className="landing min-h-dvh bg-background" data-motion={reducedMotion ? 'off' : 'on'}>
      <LandingHeader />
      <main id="main" tabIndex={-1} className="outline-none">
        <Hero />
        <Problem />
        <HowItWorks />
        <Fleets />
        <Stations />
        <Trust />
        <MadeInEgypt />
        <Pricing onChoose={chooseTier} />
        <Faq />
        <section id="demo" aria-labelledby="demo-title" className="scroll-mt-16 border-t border-border bg-surface-sunken py-20 md:py-28">
          <div className="mx-auto grid w-full max-w-6xl gap-12 px-4 md:px-6 lg:grid-cols-[0.8fr_1.2fr] lg:gap-16">
            <div data-reveal className="flex flex-col gap-8">
              <div>
                <h2 id="demo-title" className="landing-display text-3xl leading-tight font-bold text-balance md:text-[2.5rem]">
                  {t('demo.title')}
                </h2>
                <p className="mt-4 text-lg leading-relaxed text-pretty text-muted">{t('demo.lead')}</p>
              </div>
              <DirectContact />
            </div>
            <div data-reveal className="rounded-container border border-border bg-surface p-5 shadow-elevated sm:p-8">
              <DemoForm fleetSize={fleetSize} onFleetSizeChange={setFleetSize} />
            </div>
          </div>
        </section>
      </main>
      <LandingFooter />
    </div>
  );
}

function LandingFooter() {
  const { t } = useTranslation('landing');
  const year = new Date().getFullYear();
  const link = 'text-sm text-muted transition-colors hover:text-foreground';
  return (
    <footer className="border-t border-border bg-background">
      <div className="mx-auto grid w-full max-w-6xl gap-10 px-4 py-14 md:grid-cols-[1.4fr_1fr_1fr] md:px-6">
        <div className="flex flex-col gap-4">
          <BrandLogo size="md" />
          <p className="max-w-xs text-sm leading-relaxed text-muted">{t('footer.tagline')}</p>
        </div>
        <nav aria-labelledby="footer-product">
          <h2 id="footer-product" className="text-sm font-semibold">
            {t('footer.product')}
          </h2>
          <ul className="mt-4 flex flex-col gap-2.5">
            {SECTION_LINKS.map((id) => (
              <li key={id}>
                <a href={`#${id}`} className={link}>
                  {t(`nav.${id}`)}
                </a>
              </li>
            ))}
            <li>
              <Link to="/login" className={link}>
                {t('nav.signIn')}
              </Link>
            </li>
            <li>
              <Link to="/demo" className={link}>
                {t('footer.investorDemo')}
              </Link>
            </li>
          </ul>
        </nav>
        <div>
          <h2 className="text-sm font-semibold">{t('footer.company')}</h2>
          <ul className="mt-4 flex flex-col gap-2.5">
            <li>
              <a href={CONTACT.whatsappUrl} target="_blank" rel="noopener noreferrer" className={link}>
                {t('demo.direct.whatsapp')}
                {': '}
                <bdi dir="ltr">{CONTACT.whatsappDisplay}</bdi>
              </a>
            </li>
            <li>
              <a href={mailtoUrl()} className={link}>
                <bdi dir="ltr">{CONTACT.email}</bdi>
              </a>
            </li>
            <li>
              <a href="#demo" className={link}>
                {t('nav.requestDemo')}
              </a>
            </li>
          </ul>
        </div>
      </div>
      <div className="border-t border-border">
        <div className="mx-auto flex w-full max-w-6xl flex-col gap-4 px-4 py-6 md:flex-row md:items-center md:justify-between md:px-6">
          <p className="text-sm text-muted">{t('footer.rights', { year })}</p>
          <div role="group" aria-label={t('footer.preferences')} className="flex flex-wrap gap-2">
            <LanguageSwitcher />
            <ThemeSwitcher />
          </div>
        </div>
      </div>
    </footer>
  );
}
