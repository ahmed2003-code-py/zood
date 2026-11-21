import { CircleCheck, Fuel, Nfc, ReceiptText, Wallet } from 'lucide-react';
import { useEffect, useRef, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Icon, Plate, StatusChip, type LucideIcon, type Tone } from '@/design-system';
import { amountForVolume } from '@/domain/money';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatMoney, formatVolume } from '@/lib/format';
import { at, DURATION, type VignettePhase } from './vignette-phase';

/**
 * The sign-in panel's live fill-up, told in four steps: the tag is read, the litres are counted,
 * the company wallet is debited in a balanced entry, and the receipt is issued. Then it loops.
 *
 * Plain DOM and CSS. It holds still off screen and in a background tab; under reduced motion it
 * shows the finished fill-up. Every value is a sample, and the panel says so.
 */

const SAMPLE = {
  plate: 'ن ق ل 4821',
  volumeMl: 40_000,
  /** Piastres per litre. */
  unitPrice: 1_925,
  reference: 'ZW-48213',
} as const;
const TOTAL = amountForVolume(SAMPLE.volumeMl, SAMPLE.unitPrice);

const STEPS: Array<{ id: 'tag' | 'litres' | 'wallet' | 'receipt'; icon: LucideIcon }> = [
  { id: 'tag', icon: Nfc },
  { id: 'litres', icon: Fuel },
  { id: 'wallet', icon: Wallet },
  { id: 'receipt', icon: ReceiptText },
];

/** Four nodes joined by a line that fills as the fill-up moves on. Reads as a finished flow when still. */
export function StepRail({ phase, className }: { phase: VignettePhase; className?: string }) {
  const { t } = useTranslation('site');
  const reachedIndex = STEPS.reduce((last, step, index) => (at(phase, step.id) ? index : last), -1);
  return (
    <ol className={cn('relative grid grid-cols-4', className)} style={{ '--rail-progress': `${Math.max(0, reachedIndex) / 3}` } as CSSProperties}>
      <span aria-hidden className="absolute inset-x-[12.5%] top-[1.125rem] h-px bg-stage-line" />
      <span aria-hidden className="signin-rail-fill absolute inset-x-[12.5%] top-[1.125rem] h-px bg-accent" />
      {STEPS.map((step, index) => {
        const done = index <= reachedIndex;
        const current = index === reachedIndex;
        return (
          <li key={step.id} className="relative flex flex-col items-center gap-2 text-center">
            <span
              className={cn(
                'signin-rail-node flex size-9 items-center justify-center rounded-full border',
                done ? 'border-accent bg-stage text-accent' : 'border-stage-line bg-stage text-stage-muted',
                current && 'signin-rail-current',
              )}
            >
              <Icon icon={step.icon} size={17} />
            </span>
            <span className={cn('text-xs leading-tight font-medium text-balance', done ? 'text-stage-foreground' : 'text-stage-muted')}>
              {t(`panel.steps.${step.id}`)}
            </span>
          </li>
        );
      })}
    </ol>
  );
}

const STATUS: Record<VignettePhase, { key: 'reading' | 'verified' | 'pumping' | 'balanced'; tone: Tone }> = {
  idle: { key: 'reading', tone: 'info' },
  tag: { key: 'verified', tone: 'success' },
  litres: { key: 'pumping', tone: 'brand' },
  wallet: { key: 'balanced', tone: 'success' },
  receipt: { key: 'balanced', tone: 'success' },
};

/** The fill-up as the product records it: POS, vehicle, litres, ledger entry, receipt. */
export function FillUpCard({ phase }: { phase: VignettePhase }) {
  const { t } = useTranslation('site');
  const { locale } = useLocale();
  const volume = useRef<HTMLSpanElement>(null);
  const amount = useRef<HTMLSpanElement>(null);
  const bar = useRef<HTMLSpanElement>(null);

  // The pump counter writes straight to the text nodes, so counting does not re-render React.
  useEffect(() => {
    const write = (volumeMl: number) => {
      if (volume.current) volume.current.textContent = formatVolume(volumeMl, locale);
      if (amount.current) amount.current.textContent = formatMoney(amountForVolume(volumeMl, SAMPLE.unitPrice), locale);
      if (bar.current) bar.current.style.transform = `scaleX(${volumeMl / SAMPLE.volumeMl})`;
    };
    if (phase !== 'litres') {
      write(at(phase, 'wallet') ? SAMPLE.volumeMl : 0);
      return;
    }
    let frame = 0;
    const started = performance.now();
    const run = (now: number) => {
      const progress = Math.min(1, (now - started) / (DURATION.litres - 400));
      // Ease-out, like a nozzle slowing towards the end; rounded to 10 ml so the digits settle.
      const eased = 1 - (1 - progress) ** 2;
      write(Math.round((SAMPLE.volumeMl * eased) / 10) * 10);
      if (progress < 1) frame = requestAnimationFrame(run);
    };
    frame = requestAnimationFrame(run);
    return () => cancelAnimationFrame(frame);
  }, [phase, locale]);
  const status = STATUS[phase];
  const settled = at(phase, 'wallet');

  return (
    <div className="signin-card relative">
      <div className="rounded-container bg-surface-elevated p-4 text-foreground shadow-dialog xl:p-5">
        <div className="flex items-center justify-between gap-3">
          <span className="flex min-w-0 items-center gap-2.5 text-label font-medium text-muted">
            <span className="flex size-8 shrink-0 items-center justify-center rounded-control bg-primary-subtle text-primary-subtle-foreground">
              <Icon icon={Nfc} size={16} />
            </span>
            <span className="truncate">{t('panel.vignette.station')}</span>
          </span>
          <StatusChip key={status.key} tone={status.tone} label={t(`panel.vignette.${status.key}`)} className="signin-swap shrink-0" />
        </div>

        <div data-show={at(phase, 'tag')} className="signin-show mt-3.5 flex items-center justify-between gap-3">
          <Plate value={SAMPLE.plate} size="lg" />
          <span className="truncate text-sm text-muted">{t('panel.vignette.vehicle')}</span>
        </div>

        <div className="mt-3.5 rounded-control border border-border bg-surface px-4 py-2.5">
          <div className="flex items-baseline justify-between gap-3">
            <span className="text-xs font-medium text-muted">{t('panel.vignette.volume')}</span>
            <span ref={amount} className="text-sm font-semibold text-muted tabular-nums">
              {formatMoney(settled ? TOTAL : 0, locale)}
            </span>
          </div>
          <span ref={volume} dir="ltr" className="mt-0.5 block text-start text-[1.75rem] leading-tight font-bold tabular-nums">
            {formatVolume(settled ? SAMPLE.volumeMl : 0, locale)}
          </span>
          <span className="mt-2 block h-1.5 overflow-hidden rounded-full bg-surface-sunken">
            <span ref={bar} className="signin-bar block h-full rounded-full bg-primary" />
          </span>
        </div>

        {/* The ledger entry: empty until the fill-up settles, then both sides at once. */}
        <dl className="mt-3 grid gap-1 text-sm">
          <div className="flex items-baseline justify-between gap-3">
            <dt className="text-muted">{t('panel.vignette.wallet')}</dt>
            <dd key={String(settled)} className="signin-swap font-semibold tabular-nums">
              {settled ? formatMoney(-TOTAL, locale) : <span className="text-muted">—</span>}
            </dd>
          </div>
          <div className="flex items-baseline justify-between gap-3">
            <dt className="text-muted">{t('panel.vignette.payable')}</dt>
            <dd key={String(settled)} className="signin-swap font-semibold text-success tabular-nums">
              {settled ? formatMoney(TOTAL, locale, { signDisplay: 'always' }) : <span className="text-muted">—</span>}
            </dd>
          </div>
        </dl>
      </div>

      {/* The receipt slides out from under the card, like paper from the POS printer. */}
      <div className="mx-5 overflow-hidden">
        <div
          data-printed={phase === 'receipt'}
          className="signin-receipt flex items-center justify-between gap-3 rounded-b-md bg-paper px-4 pt-3 pb-3.5 text-xs text-paper-ink shadow-elevated"
        >
          <span className="flex items-center gap-1.5 font-bold">
            <Icon icon={CircleCheck} size={14} />
            {t('panel.vignette.receipt')}
          </span>
          <span className="flex items-center gap-1.5 tabular-nums">
            {t('panel.vignette.reference')} <bdi dir="ltr">{SAMPLE.reference}</bdi>
          </span>
          <span className="font-bold tabular-nums">{formatMoney(TOTAL, locale)}</span>
        </div>
      </div>
    </div>
  );
}
