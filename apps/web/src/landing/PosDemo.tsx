import { CircleCheck, Nfc, Printer, ReceiptText, Wifi } from 'lucide-react';
import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Icon, Plate, StatusChip, type LucideIcon } from '@/design-system';
import { amountForVolume } from '@/domain/money';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatMoney, formatTime, formatVolume } from '@/lib/format';
import { useOnScreen, usePrefersReducedMotion } from './hooks';

/**
 * The hero's live POS: the tag is read, the vehicle and balance are checked, the litres count up,
 * the company wallet is debited in a balanced ledger entry, and the receipt prints. Then it loops.
 *
 * Plain DOM and CSS, no images. It pauses off screen and in a background tab, and under
 * prefers-reduced-motion it shows the finished fill-up without moving. Every value is a sample,
 * labelled as an illustration.
 */

type Phase = 'ready' | 'reading' | 'verified' | 'fueling' | 'done' | 'receipt';

const ORDER: readonly Phase[] = ['ready', 'reading', 'verified', 'fueling', 'done', 'receipt'];
/** How long each phase stays on screen (ms); the last one holds before the loop restarts. */
const DURATION: Record<Phase, number> = { ready: 1100, reading: 1500, verified: 1500, fueling: 3200, done: 900, receipt: 4200 };

/** Sample values (illustration only). */
const SAMPLE = {
  plate: 'س م ر 5280',
  volumeMl: 40_000,
  /** Piastres per litre. */
  unitPrice: 1_925,
  reference: 'ZW-48213',
} as const;
const TOTAL = amountForVolume(SAMPLE.volumeMl, SAMPLE.unitPrice);

const reached = (phase: Phase, target: Phase) => ORDER.indexOf(phase) >= ORDER.indexOf(target);

export function PosDemo() {
  const { t } = useTranslation('landing');
  const { locale } = useLocale();
  const reducedMotion = usePrefersReducedMotion();
  const stage = useRef<HTMLDivElement>(null);
  const onScreen = useOnScreen(stage);
  const [cycle, setCycle] = useState<Phase>('ready');
  const [time] = useState(() => formatTime(new Date(), locale));
  const volumeText = useRef<HTMLSpanElement>(null);
  const amountText = useRef<HTMLSpanElement>(null);
  const animate = onScreen && !reducedMotion;
  const phase: Phase = reducedMotion ? 'receipt' : cycle;

  // Phase timeline: each phase schedules the next. It holds still off screen and resumes on return.
  useEffect(() => {
    if (!animate) return;
    const timer = window.setTimeout(
      () => setCycle((current) => ORDER[(ORDER.indexOf(current) + 1) % ORDER.length] ?? 'ready'),
      DURATION[cycle],
    );
    return () => window.clearTimeout(timer);
  }, [animate, cycle]);

  // The pump counter: written straight to the text nodes, so counting does not re-render React.
  useEffect(() => {
    const write = (volumeMl: number) => {
      if (volumeText.current) volumeText.current.textContent = formatVolume(volumeMl, locale);
      if (amountText.current) amountText.current.textContent = formatMoney(amountForVolume(volumeMl, SAMPLE.unitPrice), locale);
    };
    if (phase !== 'fueling') {
      write(reached(phase, 'done') ? SAMPLE.volumeMl : 0);
      return;
    }
    let frame = 0;
    const started = performance.now();
    const run = (now: number) => {
      const progress = Math.min(1, (now - started) / (DURATION.fueling - 300));
      // Ease-out, like a nozzle slowing towards the end; rounded to 10 ml so digits settle.
      const eased = 1 - (1 - progress) ** 2;
      write(Math.round((SAMPLE.volumeMl * eased) / 10) * 10);
      if (progress < 1) frame = requestAnimationFrame(run);
    };
    frame = requestAnimationFrame(run);
    return () => cancelAnimationFrame(frame);
  }, [phase, locale]);

  const scanning = phase === 'ready' || phase === 'reading';
  const steps: Array<{ id: 'read' | 'debit' | 'receipt'; icon: LucideIcon; done: boolean }> = [
    { id: 'read', icon: Nfc, done: reached(phase, 'verified') },
    { id: 'debit', icon: CircleCheck, done: reached(phase, 'done') },
    { id: 'receipt', icon: ReceiptText, done: phase === 'receipt' },
  ];

  return (
    <div className="landing-pos flex flex-col items-center gap-6">
      <p className="sr-only">{t('pos.description')}</p>
      <div ref={stage} aria-hidden className="relative h-[34rem] w-full max-w-[26rem] sm:h-[36rem]">
        <span className="absolute end-0 top-0 rounded-full border border-hero-line bg-hero-panel px-2.5 py-1 text-xs font-medium text-hero-muted">
          {t('pos.illustration')}
        </span>

        {/* The terminal: hardware bezel, a Light screen like the real POS outdoors. */}
        <div className="landing-enter-device absolute inset-x-0 top-6 mx-auto w-[15.5rem] sm:w-[16.5rem]" style={{ '--enter-delay': '250ms' } as CSSProperties}>
          <div className="rounded-[2rem] bg-device-bezel p-2.5 shadow-elevated">
            <div data-theme="light" className="relative flex h-[21rem] flex-col overflow-hidden rounded-[1.5rem] bg-background text-foreground sm:h-[22rem]">
              <div className="flex items-center justify-between border-b border-border bg-surface px-3.5 py-2 text-[0.6875rem] font-medium text-muted">
                <span className="truncate">{t('pos.station')}</span>
                <span className="flex items-center gap-1.5 tabular-nums">
                  <Icon icon={Wifi} size={12} className="text-success" />
                  {time}
                </span>
              </div>

              <div className="relative flex-1">
                {/* Scan */}
                <div data-show={scanning} className="absolute inset-0 flex flex-col items-center justify-center gap-5 px-4">
                  <div className="relative flex size-32 items-center justify-center">
                    {phase === 'reading' && (
                      <>
                        <span className="absolute inset-0 rounded-full bg-primary-subtle animate-[zood-pulse-ring_1.4s_ease-out_infinite]" />
                        <span className="absolute inset-4 rounded-full bg-primary-subtle animate-[zood-pulse-ring_1.4s_ease-out_0.4s_infinite]" />
                      </>
                    )}
                    <span className="relative flex size-20 items-center justify-center rounded-full bg-primary text-primary-foreground">
                      <Icon icon={Nfc} size={34} />
                    </span>
                  </div>
                  <p className="text-center text-base font-semibold">{t(phase === 'reading' ? 'pos.phase.reading' : 'pos.phase.ready')}</p>
                </div>

                {/* Verified → fueling → done */}
                <div data-show={!scanning} className="absolute inset-0 flex flex-col items-center gap-3 px-4 pt-4">
                  <StatusChip tone="success" size="md" label={t(reached(phase, 'done') ? 'pos.phase.done' : phase === 'fueling' ? 'pos.phase.fueling' : 'pos.phase.verified')} />
                  <Plate value={SAMPLE.plate} size="lg" />
                  <p className="text-sm font-medium text-muted">{t('pos.vehicle')}</p>
                  <div className="flex flex-wrap justify-center gap-1.5">
                    <StatusChip tone="neutral" label={t('pos.fuel')} />
                    <StatusChip tone="success" label={t('pos.walletOk')} />
                  </div>
                  <div className="mt-1 w-full rounded-control border border-border bg-surface px-3 py-2.5 text-center">
                    <p className="text-[0.6875rem] font-medium text-muted">{t('pos.volume')}</p>
                    <span ref={volumeText} dir="ltr" className="block text-[1.75rem] leading-tight font-bold tabular-nums">
                      {formatVolume(reached(phase, 'done') ? SAMPLE.volumeMl : 0, locale)}
                    </span>
                    <span ref={amountText} className="block text-sm font-semibold text-muted tabular-nums">
                      {formatMoney(reached(phase, 'done') ? TOTAL : 0, locale)}
                    </span>
                  </div>
                </div>
              </div>

              <div className="flex items-center justify-center gap-2 border-t border-border bg-surface py-2 text-[0.6875rem] text-muted">
                <Icon icon={Printer} size={13} />
                <span className="h-1 w-20 rounded-full bg-surface-sunken" />
              </div>
            </div>
          </div>
        </div>

        {/* Receipt, printed from under the terminal. */}
        <div className="absolute start-0 top-[23.25rem] w-[10.5rem] sm:inset-x-0 sm:top-[24.25rem] sm:mx-auto sm:w-[12.5rem]">
          <div
            data-printed={phase === 'receipt'}
            className="landing-pos-receipt rounded-b-md bg-paper px-3 pt-3 pb-4 sm:px-4 text-[0.6875rem] leading-relaxed text-paper-ink shadow-elevated"
          >
            <p className="text-center text-xs font-bold">{t('pos.receipt.title')}</p>
            <p className="text-center">{t('pos.station')}</p>
            <div className="my-2 border-t border-dashed border-paper-ink/40" />
            <ReceiptLine label={t('pos.receipt.reference')} value={<span dir="ltr">{SAMPLE.reference}</span>} />
            <ReceiptLine label={t('pos.receipt.vehicle')} value={<Plate value={SAMPLE.plate} variant="plain" />} />
            <ReceiptLine label={t('pos.receipt.volume')} value={<span dir="ltr">{formatVolume(SAMPLE.volumeMl, locale)}</span>} />
            <ReceiptLine label={t('pos.receipt.amount')} value={formatMoney(TOTAL, locale)} strong />
            <div className="my-2 border-t border-dashed border-paper-ink/40" />
            <p className="text-center">{t('pos.receipt.thanks')}</p>
          </div>
        </div>

        {/* The ledger entry the fill-up posts: wallet down, station payable up, balanced. */}
        <div
          data-show={reached(phase, 'done')}
          className="landing-pos-ledger absolute end-0 top-[24.5rem] w-[10.5rem] rounded-container bg-surface-elevated p-3.5 text-foreground shadow-elevated sm:-end-24 sm:top-[13rem] sm:w-[12.75rem] lg:-end-4 xl:-end-24"
        >
          <p className="text-xs font-semibold">{t('pos.ledger.title')}</p>
          <dl className="mt-2 flex flex-col gap-1.5 text-xs">
            <div className="flex items-baseline justify-between gap-2">
              <dt className="text-muted">{t('pos.ledger.wallet')}</dt>
              <dd className="font-semibold tabular-nums">{formatMoney(-TOTAL, locale)}</dd>
            </div>
            <div className="flex items-baseline justify-between gap-2">
              <dt className="text-muted">{t('pos.ledger.payable')}</dt>
              <dd className="font-semibold text-success tabular-nums">{formatMoney(TOTAL, locale, { signDisplay: 'always' })}</dd>
            </div>
          </dl>
          <p className="mt-2 flex items-center gap-1 border-t border-border pt-2 text-[0.6875rem] font-medium text-success">
            <Icon icon={CircleCheck} size={13} />
            {t('pos.ledger.balanced')}
          </p>
        </div>
      </div>

      {/* The same story as text, so the hero reads without motion. */}
      <ol className="landing-enter flex w-full max-w-[26rem] flex-wrap justify-center gap-2" style={{ '--enter-delay': '600ms' } as CSSProperties}>
        {steps.map((item) => (
          <li
            key={item.id}
            className={cn(
              'landing-pos-step flex items-center gap-1.5 rounded-full border px-3 py-1.5 text-xs font-medium',
              item.done ? 'border-accent/60 bg-hero-panel text-hero-foreground' : 'border-hero-line text-hero-muted',
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

function ReceiptLine({ label, value, strong = false }: { label: string; value: ReactNode; strong?: boolean }) {
  return (
    <div className={cn('flex items-baseline justify-between gap-2', strong && 'text-xs font-bold')}>
      <span>{label}</span>
      <span className="tabular-nums">{value}</span>
    </div>
  );
}
