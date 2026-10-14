import { ArrowRight, Building2, Fuel, Nfc, Play, ShieldCheck, SquareArrowOutUpRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { Button, Icon, LanguageSwitcher, ThemeSwitcher, toast, BrandLogo, type LucideIcon } from '@/design-system';
import { useDemoStore } from '@/mocks/store';

const EXPERIENCES: Array<{ id: 'company' | 'station' | 'ops' | 'pos'; to: string; icon: LucideIcon }> = [
  { id: 'company', to: '/company', icon: Building2 },
  { id: 'station', to: '/station', icon: Fuel },
  { id: 'ops', to: '/ops', icon: ShieldCheck },
  { id: 'pos', to: '/pos', icon: Nfc },
];

/** Opens a perspective in its own window, sized for a side-by-side live demo. */
function openWindow(path: string, name: string, width: number) {
  window.open(path, name, `popup=yes,width=${width},height=900`);
}

/** /demo — presentation entry point: the ZAWED story in one line and the four experiences. */
export function DemoLauncher() {
  const { t } = useTranslation(['demo', 'common']);
  const navigate = useNavigate();
  const reset = useDemoStore((state) => state.reset);

  const start = () => {
    reset();
    toast(t('ready'), 'info');
    navigate('/company');
  };

  return (
    <div className="flex min-h-dvh flex-col bg-background">
      <header className="bg-hero">
        <div className="mx-auto max-w-5xl px-5 pt-8 pb-20 md:px-8 md:pt-12 md:pb-28">
          <BrandLogo size="lg" tone="inverse" />
          <h1 className="mt-10 max-w-2xl text-3xl leading-tight font-semibold text-sidebar-active-foreground md:mt-14 md:text-5xl md:leading-tight">
            {t('headline')}
          </h1>
          <p className="mt-4 max-w-xl text-base text-sidebar-foreground md:text-lg">{t('intro')}</p>
          <div className="mt-8 flex flex-col gap-3 sm:flex-row sm:items-center">
            <button
              type="button"
              onClick={start}
              className="inline-flex min-h-12 items-center justify-center gap-2 rounded-control bg-accent px-6 text-base font-semibold text-accent-foreground transition-opacity hover:opacity-90"
            >
              <Icon icon={Play} size={18} />
              {t('start')}
            </button>
            <span className="text-sm text-sidebar-foreground">{t('startHint')}</span>
          </div>
        </div>
      </header>
      <main className="mx-auto -mt-12 w-full max-w-5xl flex-1 px-5 md:-mt-16 md:px-8">
        <h2 className="sr-only">{t('choose')}</h2>
        <ul className="grid gap-3 sm:grid-cols-2 md:gap-4">
          {EXPERIENCES.map((experience) => (
            <li key={experience.id}>
              <Link
                to={experience.to}
                className="group flex h-full items-start gap-4 rounded-container border border-border bg-surface p-5 transition-colors hover:border-primary md:p-6"
              >
                <span className="flex size-12 shrink-0 items-center justify-center rounded-control bg-primary-subtle text-primary-subtle-foreground">
                  <Icon icon={experience.icon} size={24} />
                </span>
                <span className="flex min-w-0 flex-1 flex-col gap-1">
                  <span className="text-lg font-semibold">{t(`experiences.${experience.id}.title`)}</span>
                  <span className="text-sm text-muted">{t(`experiences.${experience.id}.description`)}</span>
                </span>
                <span className="mt-3 text-muted transition-colors group-hover:text-primary">
                  <Icon icon={ArrowRight} directional />
                </span>
              </Link>
            </li>
          ))}
        </ul>
        <section className="mt-8 hidden flex-wrap items-center gap-3 md:flex">
          <h2 className="text-sm font-semibold text-muted">{t('twoWindows')}</h2>
          <Button variant="secondary" size="sm" leadingIcon={SquareArrowOutUpRight} onClick={() => openWindow('/company', 'zood-company', 1280)}>
            {t('openCompany')}
          </Button>
          <Button variant="secondary" size="sm" leadingIcon={SquareArrowOutUpRight} onClick={() => openWindow('/pos', 'zood-pos', 460)}>
            {t('openPos')}
          </Button>
        </section>
      </main>
      <footer className="mx-auto flex w-full max-w-5xl flex-col gap-4 px-5 py-8 md:flex-row md:items-center md:justify-between md:px-8">
        <p className="text-sm text-muted">
          {t('common:tagline')} · {t('footer')}
        </p>
        <div className="flex flex-wrap gap-2">
          <LanguageSwitcher />
          <ThemeSwitcher />
        </div>
      </footer>
    </div>
  );
}
