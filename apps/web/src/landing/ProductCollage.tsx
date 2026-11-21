import { CircleCheck, Fuel, LayoutDashboard, Nfc, Printer, ReceiptText, Wifi } from 'lucide-react';
import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import { Icon, Plate, StatusChip, type LucideIcon } from '@/design-system';
import { amountForVolume } from '@/domain/money';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatMoney, formatTime, formatVolume } from '@/lib/format';
import { usePhaseCycle } from '@/public/motion';
import { useOnScreen, usePrefersReducedMotion } from './hooks';

/**
 * The hero's product picture: one fill-up seen on three real surfaces. The station's POS reads the
 * tag and counts the litres, the company portal's statement gets the new row and the wallet goes
 * down, and the receipt prints. Built from the design system's own components (Plate, StatusChip,
 * money and volume formatting), so it is the product's look, not a screenshot. Every value is a
 * sample and the picture is labelled as an illustration.
 *
 * Drawn at a fixed design size and scaled to its column, so the composition holds from 320 px up.
 * It pauses off screen and in a background tab; under reduced motion it shows the finished fill-up.
 */

type Phase = 'ready' | 'reading' | 'verified' | 'fueling' | 'done' | 'receipt';
const ORDER: readonly Phase[] = ['ready', 'reading', 'verified', 'fueling', 'done', 'receipt'];
const DURATION: Record<Phase, number> = { ready: 1100, reading: 1500, verified: 1300, fueling: 3000, done: 1000, receipt: 4400 };
const reached = (phase: Phase, target: Phase) => ORDER.indexOf(phase) >= ORDER.indexOf(target);

/** Design size of the composition (px); it is scaled to fit its column. */
const DESIGN = { width: 460, height: 540 };
/** A little larger than drawn on wide screens, never blurry-large. */
const MAX_SCALE = 1.1;

/** Sample values (illustration only). */
const UNIT_PRICE = 1_925;
const FILL = { plate: 'ن ق ل 4821', volumeMl: 40_000, reference: 'ZW-48213' } as const;
const TOTAL = amountForVolume(FILL.volumeMl, UNIT_PRICE);
const BALANCE_BEFORE = 4_898_550;
const EARLIER = [
  { plate: 'س م ر 5280', volumeMl: 32_500, minutesAgo: 14 },
  { plate: 'م ص ر 1190', volumeMl: 54_550, minutesAgo: 52 },
  { plate: 'ط ر ق 7314', volumeMl: 28_000, minutesAgo: 95 },
] as const;

export function ProductCollage() {
  const { t } = useTranslation('landing');
  const reducedMotion = usePrefersReducedMotion();
  const frame = useRef<HTMLDivElement>(null);
  const onScreen = useOnScreen(frame);
  const cycle = usePhaseCycle(ORDER, DURATION, onScreen && !reducedMotion);
  const phase: Phase = reducedMotion ? 'receipt' : cycle;
  const scale = useFitScale(frame);

  const steps: Array<{ id: 'read' | 'debit' | 'receipt'; icon: LucideIcon; done: boolean }> = [
    { id: 'read', icon: Nfc, done: reached(phase, 'verified') },
    { id: 'debit', icon: CircleCheck, done: reached(phase, 'done') },
    { id: 'receipt', icon: ReceiptText, done: phase === 'receipt' },
  ];

  return (
    <div className="landing-collage flex w-full flex-col items-center gap-5">
      <p className="sr-only">{t('collage.description')}</p>
      <div
        ref={frame}
        aria-hidden
        className="landing-enter-device relative w-full max-w-[31.625rem]"
        style={{ height: DESIGN.height * scale, '--enter-delay': '250ms' } as CSSProperties}
      >
        <div className="absolute top-0 start-0 origin-top-left rtl:origin-top-right" style={{ width: DESIGN.width, height: DESIGN.height, transform: `scale(${scale})` }}>
          <PortalCard phase={phase} />
          <PosDevice phase={phase} />
          <Receipt printed={phase === 'receipt'} />
          <span className="absolute end-0 bottom-0 rounded-full border border-stage-line bg-stage px-2.5 py-1 text-xs font-medium text-stage-muted">
            {t('pos.illustration')}
          </span>
        </div>
      </div>

      {/* The same story as text, so the hero reads without the picture or the motion. */}
      <ol className="landing-enter flex w-full max-w-[30rem] flex-wrap justify-center gap-2" style={{ '--enter-delay': '600ms' } as CSSProperties}>
        {steps.map((item) => (
          <li
            key={item.id}
            className={cn(
              'landing-pos-step flex items-center gap-1.5 rounded-full border px-3 py-1.5 text-xs font-medium',
              item.done ? 'border-accent/60 bg-stage-panel text-stage-foreground' : 'border-stage-line text-stage-muted',
            )}
          >
            <Icon icon={item.icon} size={14} className={item.done ? 'text-accent' : undefined} />
            {t(`pos.steps.${item.id}`)}
          </li>
        ))}
      </ol>
    </div>
  );
}

/** Scales the fixed-size composition to the width of its frame. */
function useFitScale(frame: RefObject<HTMLDivElement | null>): number {
  const [scale, setScale] = useState(1);
  useLayoutEffect(() => {
    const element = frame.current;
    if (!element) return;
    const fit = () => setScale(Math.min(MAX_SCALE, element.clientWidth / DESIGN.width));
    fit();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(fit);
    observer.observe(element);
    return () => observer.disconnect();
  }, [frame]);
  return scale;
}

/** The company portal: wallet balance and the latest fill-ups, where the new one arrives. */
function PortalCard({ phase }: { phase: Phase }) {
  const { t } = useTranslation('landing');
  const { locale } = useLocale();
  const [now] = useState(() => Date.now());
  const arrived = reached(phase, 'done');
  const rows = [
    ...(arrived ? [{ plate: FILL.plate, volumeMl: FILL.volumeMl, minutesAgo: 0, fresh: true }] : []),
    ...EARLIER.map((row) => ({ ...row, fresh: false })),
  ].slice(0, 3);

  return (
    <div className="absolute top-0 end-0 w-[24.5rem] rounded-container border border-border bg-surface text-foreground shadow-dialog">
      <div className="flex items-center justify-between gap-3 border-b border-border px-4 py-3">
        <span className="flex items-center gap-2 text-label font-medium text-muted">
          <span className="flex size-7 items-center justify-center rounded-control bg-primary-subtle text-primary-subtle-foreground">
            <Icon icon={LayoutDashboard} size={15} />
          </span>
          {t('collage.portal')}
        </span>
        <StatusChip tone="success" label={t('collage.live')} />
      </div>

      <div className="px-4 pt-4">
        <span className="flex items-center gap-2 text-label font-medium text-muted">
          <span aria-hidden className="h-1 w-4 rounded-full bg-accent" />
          {t('collage.balance')}
        </span>
        <span key={String(arrived)} className="landing-collage-swap mt-1 block text-kpi font-semibold tracking-tight tabular-nums">
          {formatMoney(arrived ? BALANCE_BEFORE - TOTAL : BALANCE_BEFORE, locale)}
        </span>
      </div>

      <p className="mt-4 border-t border-border bg-surface-muted px-4 py-2 text-xs font-medium text-muted">{t('collage.latest')}</p>
      <ul className="divide-y divide-border overflow-hidden">
        {rows.map((row) => (
          <li key={row.plate} className={cn('flex h-[3.25rem] items-center gap-3 px-4', row.fresh && 'animate-[zood-arrive_1.6s_ease-out]')}>
            <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-surface-sunken text-muted">
              <Icon icon={Fuel} size={15} />
            </span>
            <span className="flex min-w-0 flex-1 flex-col">
              <Plate value={row.plate} variant="plain" className="self-start text-sm" />
              <span className="truncate text-xs text-muted">
                {t('pos.station')}
                {' · '}
                <span dir="ltr">{formatVolume(row.volumeMl, locale)}</span>
              </span>
            </span>
            <span className="flex shrink-0 flex-col items-end">
              <span className="text-sm font-semibold tabular-nums">{formatMoney(-amountForVolume(row.volumeMl, UNIT_PRICE), locale)}</span>
              <span className="text-xs text-muted tabular-nums">{formatTime(new Date(now - row.minutesAgo * 60_000), locale)}</span>
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** The station's POS (always Light, like the real one outdoors). */
function PosDevice({ phase }: { phase: Phase }) {
  const { t } = useTranslation('landing');
  const { locale } = useLocale();
  const volumeText = useRef<HTMLSpanElement>(null);
  const [time] = useState(() => formatTime(new Date(), locale));
  const scanning = phase === 'ready' || phase === 'reading';

  // The pump counter writes straight to the text node, so counting does not re-render React.
  useEffect(() => {
    const write = (volumeMl: number) => {
      if (volumeText.current) volumeText.current.textContent = formatVolume(volumeMl, locale);
    };
    if (phase !== 'fueling') {
      write(reached(phase, 'done') ? FILL.volumeMl : 0);
      return;
    }
    let frame = 0;
    const started = performance.now();
    const run = (now: number) => {
      const progress = Math.min(1, (now - started) / (DURATION.fueling - 300));
      const eased = 1 - (1 - progress) ** 2;
      write(Math.round((FILL.volumeMl * eased) / 10) * 10);
      if (progress < 1) frame = requestAnimationFrame(run);
    };
    frame = requestAnimationFrame(run);
    return () => cancelAnimationFrame(frame);
  }, [phase, locale]);

  return (
    <div className="absolute start-0 top-[17rem] w-[11.5rem]">
      <div className="rounded-[1.6rem] bg-device-bezel p-2 shadow-dialog">
        <div data-theme="light" className="flex h-[14.5rem] flex-col overflow-hidden rounded-[1.15rem] bg-background text-foreground">
          <div className="flex items-center justify-between border-b border-border bg-surface px-3 py-1.5 text-[0.625rem] font-medium text-muted">
            <span className="truncate">{t('pos.station')}</span>
            <span className="flex items-center gap-1 tabular-nums">
              <Icon icon={Wifi} size={11} className="text-success" />
              {time}
            </span>
          </div>
          <div className="relative flex-1">
            <div data-show={scanning} className="absolute inset-0 flex flex-col items-center justify-center gap-3 px-3">
              <div className="relative flex size-20 items-center justify-center">
                {phase === 'reading' && (
                  <>
                    <span className="absolute inset-0 rounded-full bg-primary-subtle animate-[zood-pulse-ring_1.4s_ease-out_infinite]" />
                    <span className="absolute inset-3 rounded-full bg-primary-subtle animate-[zood-pulse-ring_1.4s_ease-out_0.4s_infinite]" />
                  </>
                )}
                <span className="relative flex size-14 items-center justify-center rounded-full bg-primary text-primary-foreground">
                  <Icon icon={Nfc} size={26} />
                </span>
              </div>
              <p className="text-center text-xs font-semibold">{t(phase === 'reading' ? 'pos.phase.reading' : 'pos.phase.ready')}</p>
            </div>
            <div data-show={!scanning} className="absolute inset-0 flex flex-col items-center gap-2 px-3 pt-3">
              <StatusChip
                tone="success"
                label={t(reached(phase, 'done') ? 'pos.phase.done' : phase === 'fueling' ? 'pos.phase.fueling' : 'pos.phase.verified')}
              />
              <Plate value={FILL.plate} size="sm" />
              <div className="mt-1 w-full rounded-control border border-border bg-surface px-2 py-2 text-center">
                <p className="text-[0.625rem] font-medium text-muted">{t('pos.volume')}</p>
                <span ref={volumeText} dir="ltr" className="block text-xl leading-tight font-bold tabular-nums">
                  {formatVolume(reached(phase, 'done') ? FILL.volumeMl : 0, locale)}
                </span>
              </div>
              <StatusChip tone="success" label={t('pos.walletOk')} />
            </div>
          </div>
          <div className="flex items-center justify-center gap-1.5 border-t border-border bg-surface py-1.5 text-muted">
            <Icon icon={Printer} size={11} />
            <span className="h-1 w-14 rounded-full bg-surface-sunken" />
          </div>
        </div>
      </div>
    </div>
  );
}

function Receipt({ printed }: { printed: boolean }) {
  const { t } = useTranslation('landing');
  const { locale } = useLocale();
  return (
    <div className="absolute start-[12.75rem] top-[21.5rem] w-[10.5rem]">
      <div
        data-printed={printed}
        className="landing-pos-receipt rounded-md bg-paper px-3 pt-2.5 pb-3 text-[0.625rem] leading-relaxed text-paper-ink shadow-elevated"
      >
        <p className="text-center text-[0.6875rem] font-bold">{t('pos.receipt.title')}</p>
        <div className="my-1.5 border-t border-dashed border-paper-ink/40" />
        <ReceiptLine label={t('pos.receipt.reference')} value={<span dir="ltr">{FILL.reference}</span>} />
        <ReceiptLine label={t('pos.receipt.vehicle')} value={<Plate value={FILL.plate} variant="plain" />} />
        <ReceiptLine label={t('pos.receipt.volume')} value={<span dir="ltr">{formatVolume(FILL.volumeMl, locale)}</span>} />
        <ReceiptLine label={t('pos.receipt.amount')} value={formatMoney(TOTAL, locale)} strong />
        <div className="my-1.5 border-t border-dashed border-paper-ink/40" />
        <p className="text-center">{t('pos.receipt.thanks')}</p>
      </div>
    </div>
  );
}

function ReceiptLine({ label, value, strong = false }: { label: string; value: ReactNode; strong?: boolean }) {
  return (
    <div className={cn('flex items-baseline justify-between gap-2', strong && 'text-[0.6875rem] font-bold')}>
      <span>{label}</span>
      <span className="tabular-nums">{value}</span>
    </div>
  );
}
