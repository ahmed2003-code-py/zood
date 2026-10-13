import { CircleCheck, CircleX, Clock, History, LoaderCircle, Nfc, Power, Printer, TriangleAlert } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { amountForVolume, parseDecimal, volumeForAmount } from '@/domain/money';
import type { WalletStatus } from '@/domain/pos';
import { Button, Icon, Money, Numpad, Plate, SegmentedControl, StatusChip, TierChip, Volume, ZoodWordmark, type Tone } from '@/design-system';
import { ThermalReceipt } from '@/features/receipt/ThermalReceipt';
import type { ReceiptModel } from '@/features/receipt/model';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatMoney, formatNumber, formatVolume, remainingHoursMinutes } from '@/lib/format';
import { localize } from '@/lib/locale';
import { useNow } from '@/lib/use-now';
import { isToday } from '@/mocks/selectors';
import { useDemoStore } from '@/mocks/store';
import type { OkScan, PosStep } from './use-pos-flow';

/** Main content area of a POS screen: one focused task, primary action at the bottom. */
function Screen({ children, actions, className }: { children: ReactNode; actions?: ReactNode; className?: string }) {
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className={cn('flex min-h-0 flex-1 flex-col items-center gap-5 overflow-y-auto px-5 py-6 text-center', className)}>{children}</div>
      {actions && <div className="flex flex-col gap-2 px-4 pt-2 pb-4">{actions}</div>}
    </div>
  );
}

export function HomeScreen({ agentId, onScan, onHistory, onEndShift }: { agentId: string; onScan: () => void; onHistory: () => void; onEndShift: () => void }) {
  const { t } = useTranslation('pos');
  const { locale } = useLocale();
  const now = useNow();
  const transactions = useDemoStore((state) => state.data.transactions);
  const pending = useDemoStore((state) => state.posQueue.length);
  const today = useMemo(
    () => transactions.filter((txn) => txn.agentId === agentId && txn.status === 'completed' && isToday(txn.deviceAt, now)).length,
    [transactions, agentId, now],
  );
  return (
    <>
      <div className="flex min-h-0 flex-1 flex-col items-center justify-center gap-7 overflow-y-auto px-6 py-6">
        <ZoodWordmark size="md" />
        <button
          type="button"
          onClick={onScan}
          className="flex size-56 flex-col items-center justify-center gap-3 rounded-full bg-primary text-primary-foreground ring-8 ring-primary-subtle transition-transform active:scale-[0.97]"
        >
          <Icon icon={Nfc} size={56} />
          <span className="text-2xl font-semibold">{t('scan')}</span>
        </button>
        <p className="max-w-64 text-center text-sm text-muted">{t('scanHint')}</p>
      </div>
      <div className="grid grid-cols-2 gap-3 px-4 pb-4">
        <div className="rounded-control bg-surface-sunken px-4 py-3">
          <p className="text-sm text-muted">{t('today')}</p>
          <p className="text-2xl font-semibold tabular-nums">{formatNumber(today + pending, locale)}</p>
        </div>
        <div className={cn('rounded-control px-4 py-3', pending > 0 ? 'bg-warning-subtle text-warning' : 'bg-surface-sunken')}>
          <p className={cn('text-sm', pending > 0 ? 'text-warning' : 'text-muted')}>{t('pendingSync')}</p>
          <p className="text-2xl font-semibold tabular-nums" data-testid="pos-pending-count">
            {formatNumber(pending, locale)}
          </p>
        </div>
      </div>
      <footer className="grid grid-cols-2 border-t border-border">
        <button type="button" onClick={onHistory} className="flex min-h-14 items-center justify-center gap-2 font-medium transition-colors hover:bg-surface-sunken">
          <Icon icon={History} size={20} />
          {t('shiftHistory')}
        </button>
        <button
          type="button"
          onClick={onEndShift}
          className="flex min-h-14 items-center justify-center gap-2 border-s border-border font-medium transition-colors hover:bg-surface-sunken"
        >
          <Icon icon={Power} size={20} />
          {t('endShift')}
        </button>
      </footer>
    </>
  );
}

export function ScanningScreen({ onCancel }: { onCancel: () => void }) {
  const { t } = useTranslation('pos');
  return (
    <Screen
      className="justify-center"
      actions={
        <Button variant="secondary" size="lg" fullWidth onClick={onCancel}>
          {t('scanning.cancel')}
        </Button>
      }
    >
      <div className="relative flex size-48 items-center justify-center" aria-hidden>
        <span className="absolute inset-0 rounded-full bg-primary-subtle animate-[zood-pulse-ring_1.4s_ease-out_infinite]" />
        <span className="absolute inset-6 rounded-full bg-primary-subtle animate-[zood-pulse-ring_1.4s_ease-out_0.4s_infinite]" />
        <span className="relative flex size-28 items-center justify-center rounded-full bg-primary text-primary-foreground">
          <Icon icon={Nfc} size={48} />
        </span>
      </div>
      <p role="status" className="text-xl font-semibold">
        {t('scanning.title')}
      </p>
    </Screen>
  );
}

const WALLET_TONE: Record<WalletStatus, Tone> = { available: 'success', low: 'warning', insufficient: 'danger', suspended: 'danger' };

export function VerifyScreen({ scan, onContinue, onCancel }: { scan: OkScan; onContinue: () => void; onCancel: () => void }) {
  const { t } = useTranslation(['pos', 'status', 'common']);
  const { locale } = useLocale();
  const fuel = useDemoStore((state) => state.data.fuelTypes[scan.vehicle.fuelTypeId]);
  const { vehicle } = scan;
  return (
    <Screen
      actions={
        <>
          <Button size="pos" fullWidth onClick={onContinue}>
            {t('verify.continue')}
          </Button>
          <Button variant="ghost" size="lg" fullWidth onClick={onCancel}>
            {t('common:actions.cancel')}
          </Button>
        </>
      }
    >
      <StatusChip tone="success" size="md" label={t('verify.title')} />
      <Plate value={vehicle.plate} size="lg" />
      <p className="text-lg font-medium">
        {localize(vehicle.make, locale)} {localize(vehicle.model, locale)} {vehicle.year}
      </p>
      <div className="flex flex-wrap justify-center gap-2">
        <StatusChip tone="neutral" size="md" label={localize(fuel?.name, locale)} />
        <TierChip tier={vehicle.tier} />
        {scan.walletStatus && <StatusChip tone={WALLET_TONE[scan.walletStatus]} size="md" label={t(`status:wallet.${scan.walletStatus}`)} />}
      </div>
      {vehicle.tier === 'normal' && (
        <p className="text-base text-muted">
          {scan.remainingMl !== undefined ? t('verify.remaining', { value: formatVolume(scan.remainingMl, locale, 0) }) : t('verify.unlimited')}
        </p>
      )}
    </Screen>
  );
}

type EntryMode = 'liters' | 'amount';
const MAX_DIGITS = 7;

export function FuelEntryScreen({ scan, onReview, onBack }: { scan: OkScan; onReview: (volumeMl: number) => void; onBack: () => void }) {
  const { t } = useTranslation(['pos', 'common']);
  const { locale } = useLocale();
  const fuel = useDemoStore((state) => state.data.fuelTypes[scan.vehicle.fuelTypeId]);
  const [mode, setMode] = useState<EntryMode>('liters');
  const [entry, setEntry] = useState('');
  const price = scan.unitPricePiastres;

  // Liters are entered with up to 2 decimals (stored as mL), amounts with up to 2 decimals (piastres).
  const parsed = parseDecimal(entry || '0', mode === 'liters' ? 3 : 2) ?? 0;
  const volumeMl = mode === 'liters' ? parsed : price > 0 ? volumeForAmount(parsed, price) : 0;
  const amount = mode === 'liters' ? amountForVolume(parsed, price) : parsed;

  const addDigit = (digit: string) =>
    setEntry((value) => {
      const [, decimals = ''] = value.split('.');
      if (value.includes('.') && decimals.length >= 2) return value;
      if (value.replace('.', '').length >= MAX_DIGITS) return value;
      return value === '0' ? digit : value + digit;
    });

  return (
    <Screen
      className="gap-4 py-4"
      actions={
        <>
          <Button size="pos" fullWidth disabled={volumeMl <= 0} onClick={() => onReview(volumeMl)}>
            {t('fuel.review')}
          </Button>
          <Button variant="ghost" size="lg" fullWidth onClick={onBack}>
            {t('common:actions.back')}
          </Button>
        </>
      }
    >
      <div className="flex w-full items-center justify-between gap-2">
        <Plate value={scan.vehicle.plate} size="md" />
        <StatusChip tone="neutral" size="md" label={localize(fuel?.name, locale)} />
      </div>
      <SegmentedControl<EntryMode>
        label={t('fuel.mode')}
        value={mode}
        onValueChange={(next) => {
          setMode(next);
          setEntry('');
        }}
        options={[
          { value: 'liters', label: t('fuel.liters') },
          { value: 'amount', label: t('fuel.amount') },
        ]}
        className="w-full"
      />
      <div className="flex flex-col items-center gap-1" aria-live="polite">
        {mode === 'liters' ? (
          <Volume value={volumeMl} className="text-[2.75rem] leading-none font-semibold" />
        ) : (
          <Money value={amount} className="text-[2.75rem] leading-none font-semibold" />
        )}
        <span className="text-lg text-muted">{mode === 'liters' ? <Money value={amount} /> : <Volume value={volumeMl} />}</span>
        <span className="text-xs text-muted">{t('fuel.perLiter', { price: formatMoney(price, locale) })}</span>
      </div>
      <Numpad
        className="w-full"
        onDigit={addDigit}
        onDecimal={() => setEntry((value) => (value.includes('.') ? value : `${value || '0'}.`))}
        onBackspace={() => setEntry((value) => value.slice(0, -1))}
      />
    </Screen>
  );
}

const CONFIRM_DELAY_MS = 900;

/** Confirmation sheet (Normal tier) with a short anti-accidental-tap delay (PRD §6.1). */
export function ConfirmSheet({ step, onConfirm, onBack }: { step: Extract<PosStep, { name: 'confirm' }>; onConfirm: () => void; onBack: () => void }) {
  const { t } = useTranslation('pos');
  const { locale } = useLocale();
  const [armed, setArmed] = useState(false);
  const vip = step.scan.vehicle.tier === 'vip';
  useEffect(() => {
    const timer = setTimeout(() => setArmed(true), CONFIRM_DELAY_MS);
    return () => clearTimeout(timer);
  }, []);
  return (
    <div className="absolute inset-0 z-10 flex flex-col bg-overlay animate-[zood-fade-in_120ms_ease-out]">
      <div
        role="dialog"
        aria-modal="true"
        aria-label={t('confirm.title')}
        className="mt-auto flex flex-col gap-4 rounded-t-2xl bg-surface-elevated p-5 text-center animate-[zood-sheet-from-bottom_200ms_ease-out]"
      >
        <p className="text-base font-semibold text-muted">{t('confirm.title')}</p>
        <p className="text-3xl font-semibold">
          {vip ? t('confirm.vip') : t('confirm.deduct', { amount: formatMoney(step.amountPiastres, locale) })}
        </p>
        <p className="flex items-center justify-center gap-2 text-lg">
          <Volume value={step.volumeMl} />
          <span className="text-muted">·</span>
          <Plate value={step.scan.vehicle.plate} variant="plain" />
        </p>
        <button
          type="button"
          disabled={!armed}
          onClick={onConfirm}
          className="relative min-h-(--pos-button-h) overflow-hidden rounded-control bg-primary text-xl font-semibold text-primary-foreground disabled:opacity-80"
        >
          <span
            aria-hidden
            className="absolute inset-y-0 start-0 bg-primary-hover transition-[width] ease-linear"
            style={{ width: armed ? '100%' : '0%', transitionDuration: `${CONFIRM_DELAY_MS}ms` }}
          />
          <span className="relative">{t('confirm.confirm')}</span>
        </button>
        <Button variant="ghost" size="lg" onClick={onBack}>
          {t('confirm.back')}
        </Button>
      </div>
    </div>
  );
}

export function ProcessingScreen({ offline }: { offline: boolean }) {
  const { t } = useTranslation('pos');
  return (
    <Screen className="justify-center">
      <Icon icon={LoaderCircle} size={64} className="animate-spin text-primary" />
      <p role="status" className="text-xl font-semibold">
        {t(offline ? 'processing.offline' : 'processing.online')}
      </p>
    </Screen>
  );
}

type PrintState = 'idle' | 'printing' | 'printed' | 'failed';
const PRINT_MS = 1400;

/** Success, then printing. Printing never affects the financial transaction (PRD §6.1). */
export function SuccessScreen({
  receipt,
  offline,
  onFinish,
  onViewReceipt,
}: {
  receipt: ReceiptModel;
  offline: boolean;
  onFinish: () => void;
  onViewReceipt: () => void;
}) {
  const { t } = useTranslation('pos');
  const printer = useDemoStore((state) => state.simulation.printer);
  const stillPending = useDemoStore((state) => state.posQueue.some((item) => item.request.clientTxnId === receipt.clientTxnId));
  const [print, setPrint] = useState<PrintState>('idle');

  const startPrint = () => {
    setPrint('printing');
    setTimeout(() => setPrint(useDemoStore.getState().simulation.printer === 'ready' ? 'printed' : 'failed'), printer === 'ready' ? PRINT_MS : 700);
  };

  return (
    <Screen
      actions={
        print === 'failed' ? (
          <>
            <Button size="pos" fullWidth onClick={onViewReceipt}>
              {t('success.viewReceipt')}
            </Button>
            <div className="grid grid-cols-2 gap-2">
              <Button variant="secondary" size="lg" onClick={startPrint}>
                {t('success.retryPrint')}
              </Button>
              <Button variant="secondary" size="lg" onClick={onFinish}>
                {t('success.finish')}
              </Button>
            </div>
          </>
        ) : print === 'idle' ? (
          <>
            <Button size="pos" fullWidth leadingIcon={Printer} onClick={startPrint}>
              {t('success.print')}
            </Button>
            <Button variant="secondary" size="lg" fullWidth onClick={onFinish}>
              {t('success.finish')}
            </Button>
          </>
        ) : (
          <Button size="pos" fullWidth disabled={print === 'printing'} onClick={onFinish}>
            {t('success.finish')}
          </Button>
        )
      }
    >
      {print === 'printing' || print === 'printed' ? (
        <>
          <p role="status" className="flex items-center gap-2 text-base font-semibold">
            {print === 'printing' ? <Icon icon={LoaderCircle} size={18} className="animate-spin" /> : <Icon icon={CircleCheck} size={18} className="text-success" />}
            {t(print === 'printing' ? 'success.printing' : 'success.printed')}
          </p>
          <ThermalReceipt model={receipt} printing={print === 'printing'} />
        </>
      ) : (
        <>
          <span className="mt-2 flex size-20 items-center justify-center rounded-full bg-success-subtle text-success">
            <Icon icon={CircleCheck} size={48} />
          </span>
          <p className="text-2xl font-semibold" role="status">
            {t(offline ? 'success.offlineTitle' : 'success.title')}
          </p>
          {offline && (
            <StatusChip
              size="md"
              tone={stillPending ? 'warning' : 'success'}
              icon={stillPending ? Clock : CircleCheck}
              label={t(stillPending ? 'success.pendingSync' : 'success.synced')}
            />
          )}
          <p className="text-4xl font-semibold">
            {receipt.billable ? <Money value={receipt.amountPiastres} /> : <span className="text-2xl">{t('success.nonBillable')}</span>}
          </p>
          <p className="text-xl">
            <Volume value={receipt.volumeMl} />
          </p>
          <Plate value={receipt.plate} size="lg" />
          {print === 'failed' && (
            <div role="alert" className="flex w-full items-start gap-3 rounded-control bg-warning-subtle p-4 text-start text-warning">
              <Icon icon={TriangleAlert} className="mt-0.5" />
              <div>
                <p className="font-semibold">{t('success.printFailedTitle')}</p>
                <p className="text-sm">{t('success.printFailedBody')}</p>
              </div>
            </div>
          )}
        </>
      )}
    </Screen>
  );
}

export function DeclinedScreen({ step, onBack }: { step: Extract<PosStep, { name: 'declined' }>; onBack: () => void }) {
  const { t } = useTranslation('pos');
  const [now, setNow] = useState(() => new Date());
  const cooldown = step.reason === 'cooldown' && step.cooldownUntil;
  useEffect(() => {
    if (!cooldown) return;
    const timer = setInterval(() => setNow(new Date()), 15_000);
    return () => clearInterval(timer);
  }, [cooldown]);
  const remaining = step.cooldownUntil ? remainingHoursMinutes(step.cooldownUntil, now) : undefined;

  return (
    <Screen
      className="justify-center"
      actions={
        <Button size="pos" fullWidth onClick={onBack}>
          {t('decline.back')}
        </Button>
      }
    >
      <span
        className={cn(
          'flex size-20 items-center justify-center rounded-full',
          cooldown ? 'bg-warning-subtle text-warning' : 'bg-danger-subtle text-danger',
        )}
      >
        <Icon icon={cooldown ? Clock : CircleX} size={44} />
      </span>
      <p role="alert" className="text-2xl font-semibold">
        {cooldown ? t('decline.cooldownTitle') : t('decline.title')}
      </p>
      {cooldown && remaining ? (
        <div className="flex flex-col items-center gap-1">
          <span className="text-muted">{t('decline.availableIn')}</span>
          <span className="text-4xl font-semibold tabular-nums">{t('durationHm', remaining)}</span>
        </div>
      ) : (
        <p className="text-lg text-muted">{t(`decline.reasons.${step.reason}`)}</p>
      )}
      {step.plate && <Plate value={step.plate} size="md" />}
    </Screen>
  );
}

export function ReceiptScreen({ receipt, onBack }: { receipt: ReceiptModel; onBack: () => void }) {
  const { t } = useTranslation(['receipt', 'common']);
  return (
    <Screen
      className="bg-surface-sunken"
      actions={
        <Button variant="secondary" size="lg" fullWidth onClick={onBack}>
          {t('common:actions.back')}
        </Button>
      }
    >
      <p className="text-base font-semibold">{t('receipt:title')}</p>
      <ThermalReceipt model={receipt} />
    </Screen>
  );
}
