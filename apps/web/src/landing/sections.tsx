import {
  ArrowDown,
  Ban,
  Banknote,
  BellRing,
  BookOpenCheck,
  Check,
  CircleCheck,
  Clock,
  Coins,
  CreditCard,
  FileSpreadsheet,
  FileText,
  Gauge,
  HandCoins,
  History,
  Landmark,
  Languages,
  Lock,
  MessageCircle,
  Network,
  Nfc,
  ReceiptText,
  Smartphone,
  CalendarClock,
  Timer,
  TriangleAlert,
  UsersRound,
  Wallet,
  WifiOff,
  X,
  Zap,
  type LucideIcon,
} from 'lucide-react';
import type { CSSProperties, ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Icon, Plate, StatusChip } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatDate, formatMoney, formatTime, formatVolume } from '@/lib/format';
import { useNow } from './hooks';
import { PosDemo } from './PosDemo';

const CONTAINER = 'mx-auto w-full max-w-6xl px-4 md:px-6';
const delay = (ms: number) => ({ '--reveal-delay': `${ms}ms` }) as CSSProperties;
const enter = (ms: number) => ({ '--enter-delay': `${ms}ms` }) as CSSProperties;

/** A page section with its anchor, its heading and an optional lead paragraph. */
function Section({
  id,
  title,
  lead,
  className,
  headingClassName,
  children,
}: {
  id: string;
  title: string;
  lead?: string;
  className?: string;
  headingClassName?: string;
  children: ReactNode;
}) {
  return (
    <section id={id} aria-labelledby={`${id}-title`} className={cn('scroll-mt-16 py-20 md:py-28', className)}>
      <div className={CONTAINER}>
        <div data-reveal className={cn('max-w-3xl', headingClassName)}>
          <h2 id={`${id}-title`} className="landing-display text-3xl leading-tight font-bold text-balance md:text-[2.5rem]">
            {title}
          </h2>
          {lead && <p className="mt-4 text-lg leading-relaxed text-pretty text-muted">{lead}</p>}
        </div>
        {children}
      </div>
    </section>
  );
}

/** Icon, title and a sentence: the unit every feature list is built from. */
function Feature({ icon, title, body, tone = 'default', style }: { icon: LucideIcon; title: string; body: string; tone?: 'default' | 'hero'; style?: CSSProperties }) {
  return (
    <li data-reveal style={style} className="flex gap-4">
      <span
        className={cn(
          'flex size-10 shrink-0 items-center justify-center rounded-control',
          tone === 'hero' ? 'bg-hero-panel text-accent ring-1 ring-hero-line' : 'bg-primary-subtle text-primary-subtle-foreground',
        )}
      >
        <Icon icon={icon} size={20} />
      </span>
      <div className="min-w-0">
        <h3 className={cn('text-base font-semibold', tone === 'hero' && 'text-hero-foreground')}>{title}</h3>
        <p className={cn('mt-1 text-[0.9375rem] leading-relaxed', tone === 'hero' ? 'text-hero-muted' : 'text-muted')}>{body}</p>
      </div>
    </li>
  );
}

// ---------------------------------------------------------------------------------------- hero

export function Hero() {
  const { t } = useTranslation('landing');
  const assurances = ['team', 'money', 'offline'] as const;
  return (
    <section id="top" aria-labelledby="hero-title" className="landing-hero relative isolate overflow-hidden bg-hero text-hero-foreground">
      <div aria-hidden className="landing-aurora">
        <span />
        <span />
        <span />
      </div>
      <div className={cn(CONTAINER, 'grid items-center gap-12 pt-28 pb-16 md:pt-32 lg:grid-cols-[1.1fr_0.9fr] lg:gap-8 lg:pt-36 lg:pb-24')}>
        <div className="max-w-2xl">
          <h1 id="hero-title" className="landing-enter landing-display text-[2.5rem] leading-[1.15] font-bold text-balance sm:text-5xl lg:text-[3.75rem]">
            {t('hero.title')}
          </h1>
          <p className="landing-enter mt-6 max-w-xl text-lg leading-relaxed text-pretty text-hero-muted md:text-xl" style={enter(120)}>
            {t('hero.lead')}
          </p>
          <div className="landing-enter mt-9 flex flex-col gap-3 sm:flex-row" style={enter(220)}>
            <a
              href="#demo"
              className="inline-flex min-h-12 items-center justify-center rounded-control bg-accent px-6 text-base font-semibold text-accent-foreground shadow-elevated transition-[filter] hover:brightness-105"
            >
              {t('hero.primary')}
            </a>
            <a
              href="#how"
              className="inline-flex min-h-12 items-center justify-center gap-2 rounded-control border border-hero-line px-6 text-base font-semibold text-hero-foreground transition-colors hover:bg-hero-panel"
            >
              {t('hero.secondary')}
              <Icon icon={ArrowDown} size={18} />
            </a>
          </div>
          <ul className="landing-enter mt-10 flex flex-col gap-2.5 text-sm text-hero-muted sm:flex-row sm:flex-wrap sm:gap-x-6" style={enter(320)}>
            {assurances.map((id) => (
              <li key={id} className="flex items-center gap-2">
                <Icon icon={Check} size={16} className="text-accent" />
                {t(`hero.assurances.${id}`)}
              </li>
            ))}
          </ul>
        </div>
        <PosDemo />
      </div>
    </section>
  );
}

// ------------------------------------------------------------------------------------- problem

const PROBLEMS: Array<{ id: 'cards' | 'cash' | 'paper' | 'fraud'; icon: LucideIcon }> = [
  { id: 'cards', icon: CreditCard },
  { id: 'cash', icon: Banknote },
  { id: 'paper', icon: FileText },
  { id: 'fraud', icon: TriangleAlert },
];

export function Problem() {
  const { t } = useTranslation('landing');
  return (
    <Section id="problem" title={t('problem.title')} lead={t('problem.lead')}>
      <div data-reveal className="mt-12 overflow-hidden rounded-container border border-border bg-surface md:mt-14">
        <table className="w-full border-collapse text-start">
          <thead className="max-md:hidden">
            <tr className="border-b border-border text-sm">
              <td className="w-[22%] px-6 py-4" />
              <th scope="col" className="px-6 py-4 text-start font-semibold text-muted">
                {t('problem.today')}
              </th>
              <th scope="col" className="bg-primary-subtle px-6 py-4 text-start font-semibold text-primary-subtle-foreground">
                {t('problem.withUs')}
              </th>
            </tr>
          </thead>
          <tbody className="divide-y divide-border">
            {PROBLEMS.map((row) => (
              <tr key={row.id} className="max-md:block max-md:px-5 max-md:py-6">
                <th scope="row" className="px-6 py-6 text-start align-top max-md:block max-md:p-0">
                  <span className="flex items-center gap-3 text-base font-semibold">
                    <span className="flex size-9 shrink-0 items-center justify-center rounded-control bg-surface-sunken text-muted">
                      <Icon icon={row.icon} size={18} />
                    </span>
                    {t(`problem.rows.${row.id}.title`)}
                  </span>
                </th>
                <td className="px-6 py-6 align-top text-[0.9375rem] leading-relaxed text-muted max-md:mt-4 max-md:block max-md:p-0">
                  <span className="mb-1 flex items-center gap-1.5 text-xs font-semibold md:hidden">{t('problem.today')}</span>
                  <span className="flex gap-2">
                    <Icon icon={X} size={18} className="mt-0.5 text-danger" />
                    {t(`problem.rows.${row.id}.today`)}
                  </span>
                </td>
                <td className="bg-primary-subtle px-6 py-6 align-top text-[0.9375rem] leading-relaxed text-primary-subtle-foreground max-md:mt-3 max-md:block max-md:rounded-control max-md:p-4">
                  <span className="mb-1 flex text-xs font-semibold md:hidden">{t('problem.withUs')}</span>
                  <span className="flex gap-2">
                    <Icon icon={Check} size={18} className="mt-0.5 text-success" />
                    {t(`problem.rows.${row.id}.withUs`)}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Section>
  );
}

// -------------------------------------------------------------------------------- how it works

const STEPS: Array<{ id: 'topUp' | 'fuel' | 'record'; icon: LucideIcon }> = [
  { id: 'topUp', icon: Wallet },
  { id: 'fuel', icon: Nfc },
  { id: 'record', icon: ReceiptText },
];

export function HowItWorks() {
  const { t } = useTranslation('landing');
  return (
    <Section id="how" title={t('how.title')} lead={t('how.lead')} className="bg-surface">
      <div data-reveal className="relative mt-14">
        {/* The line through the three markers (desktop); the order is the information. */}
        <span aria-hidden className="landing-steps-line absolute inset-x-[16.66%] top-7 hidden h-0.5 rounded-full bg-accent md:block" />
        <ol className="relative grid gap-10 md:grid-cols-3 md:gap-8">
          {STEPS.map((step, index) => (
            <li key={step.id} className="flex gap-5 md:flex-col md:items-center md:text-center">
              <span className="relative flex size-14 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground ring-8 ring-surface">
                <Icon icon={step.icon} size={24} />
                <span className="absolute -end-1 -top-1 flex size-6 items-center justify-center rounded-full bg-accent text-xs font-bold text-accent-foreground tabular-nums">
                  {index + 1}
                </span>
              </span>
              <div className="min-w-0 md:max-w-xs">
                <h3 className="text-lg font-semibold">{t(`how.steps.${step.id}.title`)}</h3>
                <p className="mt-2 text-[0.9375rem] leading-relaxed text-muted">{t(`how.steps.${step.id}.body`)}</p>
              </div>
            </li>
          ))}
        </ol>
      </div>
    </Section>
  );
}

// ------------------------------------------------------------------------------------- fleets

const FLEET_FEATURES: Array<{ id: 'limits' | 'cooldown' | 'departments' | 'alerts' | 'statements' | 'lostTag'; icon: LucideIcon }> = [
  { id: 'limits', icon: Gauge },
  { id: 'cooldown', icon: Timer },
  { id: 'departments', icon: Network },
  { id: 'alerts', icon: BellRing },
  { id: 'statements', icon: FileSpreadsheet },
  { id: 'lostTag', icon: Ban },
];

/** A sample vehicle as the company portal shows it (illustration, sample values). */
function VehicleCard() {
  const { t } = useTranslation('landing');
  const { locale } = useLocale();
  const used = 124_500;
  const limit = 200_000;
  return (
    <figure data-reveal className="relative w-full max-w-md justify-self-center lg:sticky lg:top-24">
      <div className="rounded-container border border-border bg-surface p-5 shadow-elevated md:p-6">
        <div className="flex items-start justify-between gap-3">
          <div className="flex flex-col gap-2">
            <Plate value="ن ق ل 4821" size="lg" />
            <p className="text-sm text-muted">
              {t('pos.vehicle')}
              {' · '}
              {t('fleets.card.department')}
            </p>
          </div>
          <StatusChip tone="success" label={t('fleets.card.status')} />
        </div>
        <div className="mt-6">
          <div className="flex items-baseline justify-between gap-2 text-sm">
            <span className="font-medium">{t('fleets.card.limit')}</span>
            <span className="text-muted tabular-nums">
              {t('fleets.card.used', { used: formatVolume(used, locale, 1), limit: formatVolume(limit, locale, 0) })}
            </span>
          </div>
          <div className="mt-2 h-2.5 overflow-hidden rounded-full bg-surface-sunken">
            <div className="h-full rounded-full bg-primary" style={{ width: `${(used / limit) * 100}%` }} />
          </div>
        </div>
        <dl className="mt-6 grid gap-3 border-t border-border pt-5 text-sm">
          <div className="flex justify-between gap-3">
            <dt className="text-muted">{t('fleets.card.cooldown')}</dt>
            <dd className="font-medium">{t('fleets.card.cooldownValue')}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-muted">{t('pos.fuel')}</dt>
            <dd className="font-medium">{t('fleets.card.fuelOnly')}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-muted">{t('fleets.card.lastFueling')}</dt>
            <dd className="font-medium tabular-nums">
              {formatVolume(38_250, locale)}
              {' · '}
              {formatMoney(73_631, locale)}
            </dd>
          </div>
        </dl>
      </div>
      <figcaption className="mt-3 text-center text-xs text-muted">{t('fleets.card.label')}</figcaption>
    </figure>
  );
}

export function Fleets() {
  const { t } = useTranslation('landing');
  return (
    <Section id="fleets" title={t('fleets.title')} lead={t('fleets.lead')}>
      <div className="mt-12 grid gap-14 lg:grid-cols-[1.15fr_0.85fr] lg:items-start lg:gap-16">
        <ul className="grid gap-x-10 gap-y-9 sm:grid-cols-2">
          {FLEET_FEATURES.map((feature, index) => (
            <Feature
              key={feature.id}
              icon={feature.icon}
              title={t(`fleets.items.${feature.id}.title`)}
              body={t(`fleets.items.${feature.id}.body`)}
              style={delay((index % 2) * 90)}
            />
          ))}
        </ul>
        <VehicleCard />
      </div>
    </Section>
  );
}

// ----------------------------------------------------------------------------------- stations

const STATION_FEATURES: Array<{ id: 'paid' | 'payouts' | 'cash' | 'pos' | 'offline' | 'shifts'; icon: LucideIcon }> = [
  { id: 'paid', icon: HandCoins },
  { id: 'payouts', icon: Landmark },
  { id: 'cash', icon: Banknote },
  { id: 'pos', icon: Smartphone },
  { id: 'offline', icon: WifiOff },
  { id: 'shifts', icon: CalendarClock },
];

/** A sample station payable as the station portal shows it (illustration, sample values). */
function PayableCard() {
  const { t } = useTranslation('landing');
  const { locale } = useLocale();
  const fuelings = [
    { plate: 'س م ر 5280', amount: 77_000 },
    { plate: 'ن ق ل 4821', amount: 73_631 },
    { plate: 'م ص ر 1190', amount: 105_000 },
  ];
  return (
    <figure data-reveal className="w-full max-w-md justify-self-center lg:sticky lg:top-24">
      <div className="rounded-container border border-border bg-surface p-5 shadow-elevated md:p-6">
        <p className="text-sm text-muted">{t('stations.card.balance')}</p>
        <p className="mt-1 text-3xl font-bold tabular-nums">{formatMoney(4_821_550, locale)}</p>
        <ul className="mt-5 divide-y divide-border border-y border-border text-sm">
          {fuelings.map((row) => (
            <li key={row.plate} className="flex items-center justify-between gap-3 py-3">
              <span className="flex min-w-0 items-center gap-2">
                <Icon icon={CircleCheck} size={16} className="text-success" />
                <span className="text-muted">{t('stations.card.fueling')}</span>
                <Plate value={row.plate} variant="plain" />
              </span>
              <span className="font-semibold text-success tabular-nums">{formatMoney(row.amount, locale, { signDisplay: 'always' })}</span>
            </li>
          ))}
        </ul>
        <div className="mt-4 flex flex-wrap items-center justify-between gap-3 text-sm">
          <span className="flex items-center gap-2 font-medium">
            <Icon icon={Landmark} size={16} className="text-muted" />
            {t('stations.card.payout')}
          </span>
          <span className="font-semibold tabular-nums">{formatMoney(-3_000_000, locale)}</span>
          <StatusChip tone="warning" label={t('stations.card.awaiting')} className="w-full justify-center sm:w-auto" />
        </div>
      </div>
      <figcaption className="mt-3 text-center text-xs text-muted">{t('stations.card.label')}</figcaption>
    </figure>
  );
}

export function Stations() {
  const { t } = useTranslation('landing');
  return (
    <Section id="stations" title={t('stations.title')} lead={t('stations.lead')} className="bg-surface-sunken">
      <div className="mt-12 grid gap-14 lg:grid-cols-[0.85fr_1.15fr] lg:items-start lg:gap-16">
        <div className="order-last lg:order-first">
          <PayableCard />
        </div>
        <ul className="grid gap-x-10 gap-y-9 sm:grid-cols-2">
          {STATION_FEATURES.map((feature, index) => (
            <Feature
              key={feature.id}
              icon={feature.icon}
              title={t(`stations.items.${feature.id}.title`)}
              body={t(`stations.items.${feature.id}.body`)}
              style={delay((index % 2) * 90)}
            />
          ))}
        </ul>
      </div>
    </Section>
  );
}

// -------------------------------------------------------------------------------------- trust

const TRUST: Array<{ id: 'realtime' | 'ledger' | 'approval' | 'offline' | 'piastre' | 'limits' | 'isolation' | 'audit'; icon: LucideIcon }> = [
  { id: 'realtime', icon: Zap },
  { id: 'ledger', icon: BookOpenCheck },
  { id: 'approval', icon: UsersRound },
  { id: 'offline', icon: WifiOff },
  { id: 'piastre', icon: Coins },
  { id: 'limits', icon: Gauge },
  { id: 'isolation', icon: Lock },
  { id: 'audit', icon: History },
];

export function Trust() {
  const { t } = useTranslation('landing');
  return (
    <Section id="trust" title={t('trust.title')} lead={t('trust.lead')}>
      <ul className="mt-12 grid gap-px overflow-hidden rounded-container border border-border bg-border sm:grid-cols-2 lg:grid-cols-4">
        {TRUST.map((item, index) => (
          <li key={item.id} data-reveal style={delay((index % 4) * 70)} className="flex flex-col gap-3 bg-background p-6">
            <Icon icon={item.icon} size={22} className="text-primary" />
            <h3 className="text-base font-semibold">{t(`trust.items.${item.id}.title`)}</h3>
            <p className="text-sm leading-relaxed text-muted">{t(`trust.items.${item.id}.body`)}</p>
          </li>
        ))}
      </ul>
    </Section>
  );
}

// ----------------------------------------------------------------------------- made in Egypt

const EGYPT: Array<{ id: 'egp' | 'time' | 'arabic' | 'support' | 'invoice'; icon: LucideIcon }> = [
  { id: 'egp', icon: Banknote },
  { id: 'time', icon: Clock },
  { id: 'arabic', icon: Languages },
  { id: 'support', icon: MessageCircle },
  { id: 'invoice', icon: FileText },
];

function CairoClock() {
  const { t } = useTranslation('landing');
  const { locale } = useLocale();
  const now = useNow();
  return (
    <div className="mt-10 inline-flex flex-col rounded-container border border-hero-line bg-hero-panel px-6 py-5">
      <span className="text-sm text-hero-muted">{t('egypt.clock')}</span>
      <time dateTime={now.toISOString()} className="mt-1 text-4xl font-bold text-hero-foreground tabular-nums">
        {formatTime(now, locale)}
      </time>
      <span className="mt-1 text-sm text-hero-muted">{formatDate(now, locale)}</span>
    </div>
  );
}

export function MadeInEgypt() {
  const { t } = useTranslation('landing');
  return (
    <section id="egypt" aria-labelledby="egypt-title" className="relative isolate overflow-hidden bg-hero py-20 text-hero-foreground md:py-28">
      <div className={cn(CONTAINER, 'grid gap-12 lg:grid-cols-[0.9fr_1.1fr] lg:gap-16')}>
        <div data-reveal>
          <h2 id="egypt-title" className="landing-display text-3xl leading-tight font-bold text-balance md:text-[2.5rem]">
            {t('egypt.title')}
          </h2>
          <p className="mt-4 max-w-xl text-lg leading-relaxed text-pretty text-hero-muted">{t('egypt.lead')}</p>
          <CairoClock />
        </div>
        <ul className="grid gap-x-10 gap-y-9 sm:grid-cols-2">
          {EGYPT.map((item, index) => (
            <Feature
              key={item.id}
              tone="hero"
              icon={item.icon}
              title={t(`egypt.items.${item.id}.title`)}
              body={t(`egypt.items.${item.id}.body`)}
              style={delay((index % 2) * 90)}
            />
          ))}
        </ul>
      </div>
    </section>
  );
}
