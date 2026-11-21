import './i18n';
import '@/public/i18n';
import '@/public/stage.css';
import './landing.css';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { API_MODE } from '@/api';
import { BrandLogo, LanguageSwitcher, ThemeSwitcher } from '@/design-system';
import { cn } from '@/lib/cn';
import type { FleetSize } from '@/domain/demo-request';
import { CONTACT, mailtoUrl } from './contact';
import { DemoForm, DirectContact } from './DemoForm';
import { Faq } from './Faq';
import { LandingHeader } from './Header';
import { useThemeCrossfade } from '@/public/motion';
import { usePointerGlow, usePrefersReducedMotion, useScrollReveal } from './hooks';
import { Pricing } from './Pricing';
import { SECTION_LINKS } from './section-links';
import { Fleets, Hero, HowItWorks, MadeInEgypt, Pilot, Problem, Stations, Trust } from './sections';
import { CONTAINER } from './layout';

/** `/`: the public landing page, in both API modes. Lazy-loaded; the portals never download it. */
export function LandingPage() {
  const { t, i18n } = useTranslation('landing');
  const root = useRef<HTMLDivElement>(null);
  const reducedMotion = usePrefersReducedMotion();
  const [fleetSize, setFleetSize] = useState<FleetSize | ''>('');
  useScrollReveal(root, !reducedMotion);
  usePointerGlow(root, !reducedMotion);
  useThemeCrossfade();

  // Title and description follow the language; index.html carries the Arabic ones for crawlers.
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
    const previousDescription = meta.content;
    meta.content = t('meta.description');
    return () => {
      document.title = previousTitle;
      if (created) meta.remove();
      else meta.content = previousDescription;
    };
  }, [t, i18n.language]);

  /** A pricing tier's "Get a quote": pick its fleet size in the form and take the visitor there. */
  const chooseTier = (size: FleetSize) => {
    setFleetSize(size);
    document.getElementById('demo')?.scrollIntoView({ behavior: reducedMotion ? 'auto' : 'smooth', block: 'start' });
    document.querySelector<HTMLInputElement>('#demo input[name="companyName"]')?.focus({ preventScroll: true });
  };

  return (
    <div ref={root} className="landing public-page min-h-dvh bg-background" data-motion={reducedMotion ? 'off' : 'on'}>
      <LandingHeader />
      <main id="main" tabIndex={-1} className="outline-none">
        <Hero />
        <Problem />
        <HowItWorks />
        <Fleets />
        <Stations />
        <Trust />
        <MadeInEgypt />
        <Pilot />
        <Pricing onChoose={chooseTier} />
        <Faq />
        <section id="demo" aria-labelledby="demo-title" className="landing-settle py-20 md:py-28">
          <div className={cn(CONTAINER, 'grid gap-12 lg:grid-cols-[0.8fr_1.2fr] lg:gap-16')}>
            <div data-reveal className="flex flex-col gap-8">
              <div>
                <h2 id="demo-title" className="public-display text-3xl leading-tight font-bold text-balance md:text-[2.5rem]">
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
  const { t: tSite } = useTranslation('site');
  const year = new Date().getFullYear();
  // At least 40 px tall, so every footer link is an easy target (WCAG 2.5.8 asks for 24).
  const link = 'public-link inline-flex min-h-10 items-center text-sm text-muted hover:text-foreground';
  return (
    <footer className="landing-rule bg-surface-sunken">
      <div className={cn(CONTAINER, 'grid gap-10 py-14 sm:grid-cols-2 lg:grid-cols-[1.4fr_1fr_1fr_1fr]')}>
        <div className="flex flex-col gap-4 sm:col-span-2 lg:col-span-1">
          <BrandLogo size="md" />
          <p className="max-w-xs text-sm leading-relaxed text-muted">{t('footer.tagline')}</p>
        </div>
        <nav aria-labelledby="footer-product">
          <h2 id="footer-product" className="text-sm font-semibold">
            {t('footer.product')}
          </h2>
          <ul className="mt-3 flex flex-col">
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
            {/* The investor demo runs on mock data; against the real API it has nothing to show. */}
            {API_MODE === 'mock' && (
              <li>
                <Link to="/demo" className={link}>
                  {t('footer.investorDemo')}
                </Link>
              </li>
            )}
          </ul>
        </nav>
        <nav aria-labelledby="footer-contact">
          <h2 id="footer-contact" className="text-sm font-semibold">
            {t('footer.company')}
          </h2>
          <ul className="mt-3 flex flex-col">
            <li>
              <a href={CONTACT.whatsappUrl} target="_blank" rel="noopener noreferrer" className={link}>
                <span>
                  {t('demo.direct.whatsapp')}
                  {': '}
                  <bdi dir="ltr">{CONTACT.whatsappDisplay}</bdi>
                </span>
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
        </nav>
        <nav aria-labelledby="footer-legal">
          <h2 id="footer-legal" className="text-sm font-semibold">
            {t('footer.legal')}
          </h2>
          <ul className="mt-3 flex flex-col">
            <li>
              <Link to="/privacy" className={link}>
                {tSite('links.privacy')}
              </Link>
            </li>
            <li>
              <Link to="/terms" className={link}>
                {tSite('links.terms')}
              </Link>
            </li>
          </ul>
        </nav>
      </div>
      <div className="border-t border-border">
        <div className={cn(CONTAINER, 'flex flex-col gap-4 py-6 md:flex-row md:items-center md:justify-between')}>
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
