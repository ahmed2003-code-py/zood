import { ArrowRight, ChevronRight, LogIn, Menu, X } from 'lucide-react';
import { Dialog } from 'radix-ui';
import { useEffect, useRef, useState, type CSSProperties, type MouseEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { BrandLogo, Icon } from '@/design-system';
import { cn } from '@/lib/cn';
import { publicControlClass, type PublicTone } from '@/public/controls';
import { prefersReducedMotion } from '@/public/motion';
import { LanguageToggle, ThemeToggle } from '@/public/PreferenceButtons';
import { useActiveSection } from './hooks';
import { SECTION_LINKS, type SectionLink } from './section-links';
import { CONTAINER } from './layout';

/**
 * The landing header: a full-width bar, content on the page grid. Over the hero it is a light
 * veil on the stage; once the page scrolls it firms up into a blurred bar in the page theme. The
 * section in view is marked with the speed line. Below lg the links move into a full-screen menu
 * (focus trapped, Escape closes, the page behind does not scroll, focus returns to the button).
 */
export function LandingHeader() {
  const { t } = useTranslation('landing');
  const [scrolled, setScrolled] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const active = useActiveSection(SECTION_LINKS);
  const solid = scrolled || menuOpen;
  const tone = solid ? 'page' : 'stage';

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 24);
    onScroll();
    window.addEventListener('scroll', onScroll, { passive: true });
    return () => window.removeEventListener('scroll', onScroll);
  }, []);

  return (
    <header
      data-solid={solid || undefined}
      className={cn(
        'landing-header fixed inset-x-0 top-0 z-30 border-b',
        solid ? 'border-border bg-background/85 shadow-card backdrop-blur-xl backdrop-saturate-150' : 'border-stage-line bg-stage-panel backdrop-blur-[2px]',
      )}
    >
      <a
        href="#main"
        className="sr-only rounded-control bg-surface px-3 py-2 text-sm font-medium text-foreground focus:not-sr-only focus:absolute focus:start-3 focus:top-3 focus:z-10"
      >
        {t('a11y.skip')}
      </a>
      <div className={cn(CONTAINER, 'flex h-16 items-center gap-1 lg:h-[4.75rem]')}>
        <a href="#top" aria-label={t('a11y.home')} className="me-auto flex min-h-10 items-center rounded-control lg:me-4 xl:me-8">
          <BrandLogo size="sm" tone={solid ? 'default' : 'inverse'} className="xl:hidden" />
          <BrandLogo size="md" tone={solid ? 'default' : 'inverse'} className="max-xl:hidden" />
        </a>

        <nav aria-label={t('a11y.primaryNav')} className="me-auto hidden lg:block">
          <ul className="flex items-center">
            {SECTION_LINKS.map((id) => (
              <li key={id}>
                <a
                  href={`#${id}`}
                  aria-current={active === id ? 'true' : undefined}
                  className={cn(
                    'relative inline-flex min-h-11 items-center rounded-control px-2.5 text-[0.9375rem] font-medium transition-colors xl:px-4 xl:text-base',
                    solid
                      ? active === id
                        ? 'text-foreground'
                        : 'text-muted hover:bg-surface-sunken hover:text-foreground'
                      : active === id
                        ? 'text-stage-foreground'
                        : 'text-stage-muted hover:bg-stage-panel hover:text-stage-foreground',
                  )}
                >
                  {t(`nav.${id}`)}
                  <span
                    aria-hidden
                    className={cn(
                      'landing-nav-mark absolute inset-x-0 bottom-0.5 mx-auto h-1 w-5 rounded-full bg-accent',
                      active === id ? 'opacity-100' : 'scale-x-0 opacity-0',
                    )}
                  />
                </a>
              </li>
            ))}
          </ul>
        </nav>

        <div className="hidden items-center gap-0.5 md:flex">
          <LanguageToggle tone={tone} className="xl:text-[0.9375rem]" />
          <ThemeToggle tone={tone} />
        </div>
        <Link to="/login" className={publicControlClass(tone, 'px-3 max-lg:hidden xl:text-[0.9375rem]')}>
          <Icon icon={LogIn} size={17} directional />
          {t('nav.signIn')}
        </Link>
        <a
          href="#demo"
          className="public-cta ms-1.5 inline-flex min-h-10 items-center gap-1.5 rounded-control bg-accent px-3.5 text-sm font-semibold whitespace-nowrap text-accent-foreground shadow-control sm:px-4 lg:min-h-11 xl:ms-3 xl:px-5 xl:text-[0.9375rem] pointer-coarse:min-h-11"
        >
          {t('nav.requestDemo')}
          <Icon icon={ArrowRight} size={16} directional className="public-cta-arrow hidden sm:block" />
        </a>
        <MobileMenu open={menuOpen} onOpenChange={setMenuOpen} active={active} tone={tone} />
      </div>
    </header>
  );
}

function MobileMenu({
  open,
  onOpenChange,
  active,
  tone,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  active: SectionLink | null;
  tone: PublicTone;
}) {
  const { t } = useTranslation('landing');
  // A section link closes the menu, then focus and scroll go to that section, not back to the button.
  const target = useRef<HTMLElement | null>(null);

  const go = (event: MouseEvent<HTMLAnchorElement>, id: string) => {
    event.preventDefault();
    target.current = document.getElementById(id);
    onOpenChange(false);
  };

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Trigger asChild>
        <button type="button" aria-label={t('a11y.menu')} className={publicControlClass(tone, 'ms-1 w-10 lg:hidden pointer-coarse:w-11')}>
          <Icon icon={Menu} size={22} />
        </button>
      </Dialog.Trigger>
      <Dialog.Portal>
        {/* Radix locks the page's scrolling through the overlay, so the menu keeps one under it. */}
        <Dialog.Overlay className="landing-menu fixed inset-0 z-40 bg-background lg:hidden" />
        <Dialog.Content
          aria-describedby={undefined}
          onCloseAutoFocus={(event) => {
            const section = target.current;
            target.current = null;
            if (!section) return;
            event.preventDefault();
            section.setAttribute('tabindex', '-1');
            section.focus({ preventScroll: true });
            section.scrollIntoView({ behavior: prefersReducedMotion() ? 'auto' : 'smooth', block: 'start' });
            history.replaceState(null, '', `#${section.id}`);
          }}
          className="landing-menu fixed inset-0 z-40 flex flex-col overflow-y-auto bg-background text-foreground lg:hidden"
        >
          <Dialog.Title className="sr-only">{t('a11y.menu')}</Dialog.Title>
          <div className="sticky top-0 z-10 border-b border-border bg-background">
            <div className="mx-auto flex h-16 w-full max-w-6xl items-center gap-1 px-4 md:px-6">
              <BrandLogo size="sm" className="me-auto" />
              <LanguageToggle tone="page" />
              <ThemeToggle tone="page" />
              <Dialog.Close asChild>
                <button type="button" aria-label={t('a11y.closeMenu')} className={publicControlClass('page', 'ms-1 w-10 pointer-coarse:w-11')}>
                  <Icon icon={X} size={22} />
                </button>
              </Dialog.Close>
            </div>
          </div>

          <nav aria-label={t('a11y.primaryNav')} className="mx-auto w-full max-w-6xl flex-1 px-4 pt-4 pb-6 md:px-6">
            <ul className="flex flex-col">
              {SECTION_LINKS.map((id, index) => (
                <li key={id} className="landing-menu-item border-b border-border" style={{ '--item': index } as CSSProperties}>
                  <a
                    href={`#${id}`}
                    aria-current={active === id ? 'true' : undefined}
                    onClick={(event) => go(event, id)}
                    className="flex min-h-15 items-center gap-3 text-xl font-semibold sm:text-2xl"
                  >
                    <span aria-hidden className={cn('h-1 w-4 shrink-0 rounded-full', active === id ? 'bg-accent' : 'bg-transparent')} />
                    <span className="flex-1">{t(`nav.${id}`)}</span>
                    <Icon icon={ChevronRight} size={20} directional className="text-muted" />
                  </a>
                </li>
              ))}
            </ul>
          </nav>

          <div className="sticky bottom-0 border-t border-border bg-background pb-[env(safe-area-inset-bottom)]">
            <div className="mx-auto flex w-full max-w-6xl flex-col gap-2.5 px-4 py-4 sm:flex-row md:px-6">
              <a
                href="#demo"
                onClick={(event) => go(event, 'demo')}
                className="inline-flex min-h-12 flex-1 items-center justify-center gap-2 rounded-control bg-accent px-5 text-base font-semibold text-accent-foreground"
              >
                {t('nav.requestDemo')}
                <Icon icon={ArrowRight} size={18} directional />
              </a>
              <Link
                to="/login"
                className="inline-flex min-h-12 flex-1 items-center justify-center gap-2 rounded-control border border-border-strong px-5 text-base font-semibold"
              >
                <Icon icon={LogIn} size={18} directional />
                {t('nav.signIn')}
              </Link>
            </div>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
