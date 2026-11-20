/**
 * The live fueling flow (B4 contract): home → tag → verify → volume → confirm → recorded | declined,
 * with the receipt, the shift history and the end-of-shift summary.
 *
 * Verify and the early declines come from the terminal's snapshot (flow.ts). Online, the server
 * still decides the fueling; a refusal the snapshot predicts is checked against a fresh snapshot
 * first, so a limit raised a minute ago does not stop the agent. Offline, the terminal decides and
 * queues the capture (B5 contract §2).
 */
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, CircleCheck, CircleX, Clock, CloudOff, Fuel, History, LoaderCircle, Nfc, Power, Printer, Share2, TriangleAlert } from 'lucide-react';
import { ApiRequestError, NETWORK_ERROR } from '@/api/http-client';
import { Banner, Button, Icon, Money, Numpad, Plate, SegmentedControl, StatusChip, Volume, toast, type Tone } from '@/design-system';
import { parseDecimal, volumeForAmount } from '@/domain/money';
import { ThermalReceipt } from '@/features/receipt/ThermalReceipt';
import type { ReceiptModel } from '@/features/receipt/model';
import { i18n } from '@/i18n';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatMoney, formatNumber, formatTime, formatVolume } from '@/lib/format';
import type { Locale } from '@/lib/locale';
import { checkTag, formatCountdown, pumpFuelBlock, quote, type LimitLeft, type TagCheck, type WalletWord } from './flow';
import { normaliseUid, tagDigest, type FuelType, type OfflineDecline, type QueuedCapture } from './offline';
import { DEVICE_ENDED, recordFueling, SESSION_ENDED, type AgentSession, type DeviceIdentity, type PosFueling } from './pos-api';
import { clearShift, loadShift, recordShiftEntry, summariseShift, type ShiftEntry } from './shift-log';
import { SIMULATOR_TOOLS, rememberTag } from './sim-tools';
import { SimulatorTagPicker } from './SimulatorTagPicker';
import { DisplayPreferences, PosHeader, PosMenu, ResultMark, Screen, ShiftMenuActions, Step, SyncPanel } from './ui';
import { useErrorText } from './use-error-text';
import type { OfflineState } from './use-offline';

/** A tag that was read, with what the terminal knows about it. */
interface TagRead {
  uid: string;
  check: TagCheck;
}

interface Decline {
  reason: OfflineDecline | 'NO_SNAPSHOT';
  plate?: string;
  cooldownUntil?: string;
  /** The vehicle's fuel, for a wrong-fuel decline. */
  fuelType?: FuelType;
  offline: boolean;
}

type FuelStep =
  | { name: 'home' }
  | { name: 'tag' }
  | { name: 'checking' }
  | { name: 'verify'; read: TagRead }
  | { name: 'volume'; read: TagRead; pumpFuel?: FuelType }
  | { name: 'confirm'; read: TagRead; pumpFuel?: FuelType; volumeMl: number; clientTxnId: string }
  | { name: 'sending'; offline: boolean }
  | { name: 'failed'; read: TagRead; pumpFuel?: FuelType; volumeMl: number; clientTxnId: string; error: string }
  | { name: 'declined'; decline: Decline }
  | { name: 'done'; entry: ShiftEntry; walletStatus?: WalletWord }
  | { name: 'receipt'; entry: ShiftEntry; back: 'history' }
  | { name: 'history' }
  | { name: 'summary' };

const MAX_VOLUME_ML = 1_500_000;
const CONFIRM_DELAY_MS = 900;
const PRINT_MS = 1400;

const WALLET_TONE: Record<WalletWord, Tone> = { AVAILABLE: 'success', LOW: 'warning', INSUFFICIENT: 'danger', SUSPENDED: 'danger' };

/** The terminal's short reference for an offline capture, until the server gives its own. */
const offlineReference = (clientTxnId: string) => clientTxnId.slice(0, 8).toUpperCase();

function useFuelName() {
  const { t } = useTranslation('live');
  return (fuel: FuelType | undefined) => (fuel ? t(`enums.fuel.${fuel}`) : '');
}

function entryFromFueling(clientTxnId: string, fueling: PosFueling): ShiftEntry {
  return {
    id: clientTxnId,
    at: fueling.evaluatedAt,
    plate: fueling.plate,
    fuelType: fueling.fuelType,
    volumeMl: fueling.volumeMl,
    amountPiastres: fueling.status === 'COMPLETED' ? fueling.amountPiastres : 0,
    unitPricePiastres: fueling.unitPricePiastres,
    billable: fueling.billable,
    status: fueling.status,
    declineReason: fueling.declineReason,
    reference: fueling.reference,
    receiptPath: fueling.status === 'COMPLETED' ? fueling.receiptPath : undefined,
    receiptLocale: fueling.receiptLocale === 'en' ? 'en' : 'ar',
    stationNameAr: fueling.stationNameAr,
    stationNameEn: fueling.stationNameEn,
  };
}

function entryFromCapture(capture: QueuedCapture, station?: { nameAr: string; nameEn: string }): ShiftEntry {
  return {
    id: capture.clientTxnId,
    at: capture.deviceAt,
    plate: capture.plate,
    fuelType: capture.fuelType,
    volumeMl: capture.volumeMl,
    amountPiastres: capture.amountPiastres,
    unitPricePiastres: capture.unitPricePiastres,
    billable: capture.billable,
    status: 'QUEUED',
    reference: offlineReference(capture.clientTxnId),
    receiptLocale: capture.receiptLocale,
    stationNameAr: station?.nameAr,
    stationNameEn: station?.nameEn,
  };
}

/** The public receipt link on this web origin (the API path without its prefix). */
const receiptUrl = (path: string) => `${window.location.origin}${path.replace(/^\/api\/v1/, '')}`;

function receiptOf(entry: ShiftEntry): ReceiptModel {
  const fuel = (locale: Locale) => (entry.fuelType ? i18n.getFixedT(locale, 'live')(`enums.fuel.${entry.fuelType}`) : '');
  return {
    reference: entry.reference,
    clientTxnId: entry.id,
    qrUrl: entry.receiptPath ? receiptUrl(entry.receiptPath) : undefined,
    stationName: { ar: entry.stationNameAr ?? '', en: entry.stationNameEn ?? '' },
    timeZone: 'Africa/Cairo',
    at: entry.at,
    plate: entry.plate,
    fuelName: { ar: fuel('ar'), en: fuel('en') },
    volumeMl: entry.volumeMl,
    unitPricePiastres: entry.unitPricePiastres,
    amountPiastres: entry.amountPiastres,
    billable: entry.billable,
    locale: entry.receiptLocale,
    pendingSync: entry.status === 'QUEUED',
  };
}

export function FuelFlow({
  device,
  session,
  offline,
  onSignOut,
  onSessionEnded,
  onDeviceEnded,
}: {
  device: DeviceIdentity;
  session: AgentSession;
  offline: OfflineState;
  onSignOut: () => void;
  onSessionEnded: () => void;
  onDeviceEnded: () => void;
}) {
  const { t } = useTranslation('pos');
  const { locale } = useLocale();
  const errorText = useErrorText();
  const [step, setStep] = useState<FuelStep>({ name: 'home' });
  const who = useMemo(
    () => ({ deviceId: device.deviceId, agentId: session.agent.id, sessionExpiresAt: session.expiresAt }),
    [device.deviceId, session.agent.id, session.expiresAt],
  );
  const [entries, setEntries] = useState<ShiftEntry[]>(() => loadShift(who).entries);
  const record = (entry: ShiftEntry) => setEntries(recordShiftEntry(who, entry));
  const receiptLocale: 'ar' | 'en' = i18n.language === 'en' ? 'en' : 'ar';
  const station = offline.snapshot?.station;
  const pendingIds = useMemo(() => new Set(offline.queue.map((item) => item.clientTxnId)), [offline.queue]);

  /** Reads a tag: looks it up in the snapshot, refreshing it once online before trusting a refusal. */
  const readTag = async (raw: string) => {
    const uid = normaliseUid(raw);
    if (!uid) return;
    if (SIMULATOR_TOOLS) rememberTag(uid);
    setStep({ name: 'checking' });
    const lookUp = async (snapshot = offline.snapshot) =>
      checkTag(snapshot, [...offline.queue, ...offline.synced], snapshot ? await tagDigest(snapshot.tagKey, uid) : '', offline.serverNow());
    let check = await lookUp();
    if (offline.online && (!check.known || check.block)) {
      const fresh = await offline.refreshSnapshot();
      if (fresh) check = await lookUp(fresh.snapshot);
    }
    if (check.known && check.block) {
      return setStep({ name: 'declined', decline: { reason: check.block, plate: check.plate, cooldownUntil: check.cooldownUntil, offline: !offline.online } });
    }
    // Online, a tag the terminal cannot check goes to the server, which knows every tag.
    if (!check.known && (!offline.online || check.reason === 'STATION_CLOSED')) {
      return setStep({ name: 'declined', decline: { reason: check.reason, offline: !offline.online } });
    }
    setStep({ name: 'verify', read: { uid, check } });
  };

  const captureOffline = async (current: Extract<FuelStep, { name: 'confirm' | 'failed' }>, fromAttempt = false) => {
    if (new Date(session.expiresAt).getTime() <= offline.serverNow().getTime()) return onSessionEnded();
    const { read, volumeMl, clientTxnId } = current;
    try {
      const outcome = await offline.captureOffline({ clientTxnId, tagUid: read.uid, volumeMl, agentId: session.agent.id, receiptLocale });
      if ('noSnapshot' in outcome) {
        if (fromAttempt) await offline.settleAttempt(clientTxnId, 'REFUSED');
        setStep({ ...current, name: 'failed', error: t('live.offline.noSnapshot') });
      } else if (!outcome.decision.ok) {
        if (fromAttempt) await offline.settleAttempt(clientTxnId, 'REFUSED');
        const { reason, plate, cooldownUntil } = outcome.decision;
        setStep({ name: 'declined', decline: { reason, plate, cooldownUntil, offline: true } });
      } else if (outcome.capture) {
        if (fromAttempt) await offline.settleAttempt(clientTxnId, 'QUEUED');
        const entry = entryFromCapture(outcome.capture, station);
        record(entry);
        setStep({ name: 'done', entry, walletStatus: read.check.known ? read.check.walletStatus : undefined });
      }
    } catch (cause) {
      // The capture could not be saved on the terminal: say so; never claim it is done.
      setStep({ ...current, name: 'failed', error: errorText(cause) });
    }
  };

  /**
   * Sends the fueling. A retry after a lost answer reuses the same clientTxnId, so it is charged
   * once; with no answer at all it falls back to the offline queue under that same id.
   */
  const send = async (current: Extract<FuelStep, { name: 'confirm' | 'failed' }>) => {
    const { read, volumeMl, clientTxnId, pumpFuel } = current;
    setStep({ name: 'sending', offline: !offline.online });
    if (!offline.online) return captureOffline(current);
    await offline.beginAttempt({ clientTxnId, tagUid: read.uid, volumeMl });
    try {
      const fueling = await recordFueling(device, session, {
        clientTxnId,
        tagUid: read.uid,
        volumeMl,
        fuelType: pumpFuel,
        deviceAt: offline.serverNow().toISOString(),
        receiptLocale,
      });
      await offline.settleAttempt(clientTxnId, 'ANSWERED');
      const entry = entryFromFueling(clientTxnId, fueling);
      record(entry);
      if (fueling.status === 'COMPLETED') setStep({ name: 'done', entry, walletStatus: fueling.walletStatus });
      else
        setStep({
          name: 'declined',
          decline: {
            reason: fueling.declineReason ?? 'TAG_INACTIVE',
            plate: fueling.plate,
            cooldownUntil: fueling.cooldownUntil,
            fuelType: read.check.known ? read.check.fuelType : undefined,
            offline: false,
          },
        });
    } catch (cause) {
      if (cause instanceof ApiRequestError && cause.code === NETWORK_ERROR) {
        offline.noteFailure(cause);
        return captureOffline(current, true);
      }
      await offline.settleAttempt(clientTxnId, 'ANSWERED');
      if (cause instanceof ApiRequestError) {
        if (DEVICE_ENDED.has(cause.code)) return onDeviceEnded();
        if (SESSION_ENDED.has(cause.code)) return onSessionEnded();
        if (cause.code === 'TAG_NOT_RECOGNISED') return setStep({ name: 'declined', decline: { reason: 'UNKNOWN_TAG', offline: false } });
      }
      setStep({ ...current, name: 'failed', error: errorText(cause) });
    }
  };

  const home = () => setStep({ name: 'home' });
  const summary = summariseShift(entries, pendingIds);
  const title = station ? (locale === 'ar' ? station.nameAr : station.nameEn) : t('live.login.device', { code: device.deviceCode });

  const body = (() => {
    switch (step.name) {
      case 'home':
        return (
          <HomeScreen
            fuelings={summary.fuelings}
            volumeMl={summary.volumeMl}
            waiting={offline.queue.length}
            onScan={() => setStep({ name: 'tag' })}
            onHistory={() => setStep({ name: 'history' })}
            onEndShift={() => setStep({ name: 'summary' })}
          />
        );
      case 'tag':
        return <TagScreen snapshot={offline.snapshot} onRead={(uid) => void readTag(uid)} onCancel={home} />;
      case 'checking':
        return <BusyScreen label={t('live.tag.checking')} />;
      case 'verify':
        return (
          <VerifyScreen
            read={step.read}
            online={offline.online}
            onContinue={() => setStep({ name: 'volume', read: step.read, pumpFuel: step.read.check.known ? step.read.check.fuelType : undefined })}
            onCancel={home}
          />
        );
      case 'volume':
      case 'confirm': {
        const current = step;
        return (
          <>
            <VolumeScreen
              read={current.read}
              pumpFuel={current.pumpFuel}
              stationFuels={station?.fuelTypes ?? []}
              onPumpFuel={(pumpFuel) => {
                const { check } = current.read;
                const block = check.known && pumpFuel ? pumpFuelBlock(check, pumpFuel) : undefined;
                if (block && check.known) {
                  return setStep({ name: 'declined', decline: { reason: block, plate: check.plate, fuelType: check.fuelType, offline: !offline.online } });
                }
                setStep({ name: 'volume', read: current.read, pumpFuel });
              }}
              onReview={(volumeMl) => setStep({ name: 'confirm', read: current.read, pumpFuel: current.pumpFuel, volumeMl, clientTxnId: crypto.randomUUID() })}
              onBack={home}
            />
            {current.name === 'confirm' && (
              <ConfirmSheet
                read={current.read}
                volumeMl={current.volumeMl}
                pumpFuel={current.pumpFuel}
                online={offline.online}
                onConfirm={() => void send(current)}
                onBack={() => setStep({ name: 'volume', read: current.read, pumpFuel: current.pumpFuel })}
              />
            )}
          </>
        );
      }
      case 'sending':
        return <BusyScreen label={t(step.offline ? 'processing.offline' : 'live.fueling.sending')} />;
      case 'failed':
        return <FailedScreen error={step.error} onRetry={() => void send(step)} onHome={home} />;
      case 'declined':
        return <DeclinedScreen decline={step.decline} onBack={home} />;
      case 'done':
        return <DoneScreen entry={step.entry} walletStatus={step.walletStatus} pending={pendingIds.has(step.entry.id)} onFinish={home} />;
      case 'receipt':
        return <ReceiptScreen entry={step.entry} onBack={() => setStep({ name: 'history' })} />;
      case 'history':
        return (
          <HistoryScreen
            entries={entries}
            pendingIds={pendingIds}
            onOpen={(entry) => setStep({ name: 'receipt', entry, back: 'history' })}
            onSummary={() => setStep({ name: 'summary' })}
            onBack={home}
          />
        );
      case 'summary':
        return (
          <ShiftSummaryScreen
            entries={entries}
            pendingIds={pendingIds}
            onEnd={() => {
              clearShift();
              onSignOut();
            }}
            onBack={home}
          />
        );
    }
  })();

  // The menu opens over whatever step is on screen; history and end of shift leave it.
  return (
    <>
      <PosHeader
        title={title}
        agent={{ name: session.agent.fullName, workerId: session.agent.workerId }}
        state={offline}
        menu={(close) => (
          <>
            <ShiftMenuActions
              onHistory={() => {
                close();
                setStep({ name: 'history' });
              }}
              onEndShift={() => {
                close();
                setStep({ name: 'summary' });
              }}
            />
            <DisplayPreferences />
            <SyncPanel state={offline} />
          </>
        )}
      />
      <Step id={step.name === 'confirm' ? 'volume' : step.name}>{body}</Step>
    </>
  );
}

// ------------------------------------------------------------------------------------- screens

function BusyScreen({ label }: { label: string }) {
  return (
    <Screen className="justify-center">
      <Icon icon={LoaderCircle} size={56} className="animate-spin text-primary" />
      <p role="status" className="text-xl font-semibold">
        {label}
      </p>
    </Screen>
  );
}

function HomeScreen({
  fuelings,
  volumeMl,
  waiting,
  onScan,
  onHistory,
  onEndShift,
}: {
  fuelings: number;
  volumeMl: number;
  waiting: number;
  onScan: () => void;
  onHistory: () => void;
  onEndShift: () => void;
}) {
  const { t } = useTranslation('pos');
  const { locale } = useLocale();
  return (
    <>
      <div className="flex min-h-0 flex-1 flex-col items-center justify-center gap-6 overflow-y-auto px-6 py-6">
        <button
          type="button"
          onClick={onScan}
          className="flex size-52 flex-col items-center justify-center gap-3 rounded-full bg-primary text-primary-foreground shadow-elevated ring-8 ring-primary-subtle transition-transform duration-150 active:scale-[0.97]"
        >
          <Icon icon={Nfc} size={56} />
          <span className="text-2xl font-semibold">{t('scan')}</span>
        </button>
        <p className="max-w-64 text-center text-sm text-muted">{t('scanHint')}</p>
      </div>
      <div className="grid grid-cols-2 gap-3 px-4 pb-4">
        <div className="rounded-control border border-border bg-surface px-4 py-3">
          <p className="text-sm text-muted">{t('live.home.thisShift')}</p>
          <p className="text-2xl font-semibold tabular-nums">{formatNumber(fuelings, locale)}</p>
          <p className="text-xs text-muted tabular-nums">{formatVolume(volumeMl, locale, 0)}</p>
        </div>
        <div className={cn('rounded-control border px-4 py-3', waiting > 0 ? 'border-warning/30 bg-warning-subtle text-warning' : 'border-border bg-surface')}>
          <p className={cn('text-sm', waiting > 0 ? 'text-warning' : 'text-muted')}>{t('pendingSync')}</p>
          <p className="text-2xl font-semibold tabular-nums" data-testid="pos-pending-count">
            {formatNumber(waiting, locale)}
          </p>
        </div>
      </div>
      <footer className="grid grid-cols-2 border-t border-border bg-surface">
        <button type="button" onClick={onHistory} className="flex min-h-16 items-center justify-center gap-2 font-medium transition-colors hover:bg-surface-hover">
          <Icon icon={History} size={20} />
          {t('shiftHistory')}
        </button>
        <button
          type="button"
          onClick={onEndShift}
          className="flex min-h-16 items-center justify-center gap-2 border-s border-border font-medium transition-colors hover:bg-surface-hover"
        >
          <Icon icon={Power} size={20} />
          {t('endShift')}
        </button>
      </footer>
    </>
  );
}

function TagScreen({ snapshot, onRead, onCancel }: { snapshot: OfflineState['snapshot']; onRead: (uid: string) => void; onCancel: () => void }) {
  const { t } = useTranslation('pos');
  const [uid, setUid] = useState('');
  const clean = normaliseUid(uid);
  return (
    <Screen
      actions={
        <>
          <Button size="pos" fullWidth disabled={!clean} onClick={() => clean && onRead(clean)}>
            {t('live.tag.read')}
          </Button>
          <Button variant="ghost" size="lg" fullWidth onClick={onCancel}>
            {t('scanning.cancel')}
          </Button>
        </>
      }
    >
      <div className="relative flex size-28 shrink-0 items-center justify-center" aria-hidden>
        <span className="absolute inset-0 rounded-full bg-primary-subtle animate-[zood-pulse-ring_1.4s_ease-out_infinite]" />
        <span className="relative flex size-20 items-center justify-center rounded-full bg-primary text-primary-foreground">
          <Icon icon={Nfc} size={40} />
        </span>
      </div>
      <h1 className="text-xl font-semibold">{t('live.tag.title')}</h1>
      <p className="text-sm text-muted">{t('live.tag.hint')}</p>
      <form
        className="w-full text-start"
        onSubmit={(event) => {
          event.preventDefault();
          if (clean) onRead(clean);
        }}
      >
        <label className="flex flex-col gap-1.5">
          <span className="text-sm font-medium">{t('live.tag.uid')}</span>
          <input
            value={uid}
            onChange={(event) => setUid(event.target.value)}
            dir="ltr"
            inputMode="text"
            autoCapitalize="characters"
            autoComplete="off"
            spellCheck={false}
            className="min-h-14 w-full rounded-control border border-input-border bg-input px-4 font-mono text-xl tracking-wider outline-none focus-visible:border-focus-ring focus-visible:ring-3 focus-visible:ring-focus-ring/25"
          />
        </label>
      </form>
      {SIMULATOR_TOOLS && <SimulatorTagPicker snapshot={snapshot} onPick={onRead} />}
    </Screen>
  );
}

/** One line of the verify list: what it is, and its value at the inline end. */
function Fact({ label, children, tone }: { label: string; children: ReactNode; tone?: 'warning' | 'danger' }) {
  return (
    <div className="flex items-center justify-between gap-3 py-2.5">
      <dt className="text-sm text-muted">{label}</dt>
      <dd className={cn('text-end text-base font-semibold', tone === 'warning' && 'text-warning', tone === 'danger' && 'text-danger')}>{children}</dd>
    </div>
  );
}

function useLimitText() {
  const { t } = useTranslation('pos');
  const { locale } = useLocale();
  return (limit: LimitLeft) =>
    t(`live.verify.left.${limit.period}`, {
      value: limit.kind === 'VOLUME' ? formatVolume(limit.remaining, locale, 0) : formatMoney(limit.remaining, locale),
    });
}

function VerifyScreen({ read, online, onContinue, onCancel }: { read: TagRead; online: boolean; onContinue: () => void; onCancel: () => void }) {
  const { t } = useTranslation(['pos', 'common']);
  const { locale } = useLocale();
  const fuelName = useFuelName();
  const limitText = useLimitText();
  const { check } = read;
  return (
    <Screen
      className="gap-3"
      actions={
        <>
          <Button size="pos" fullWidth leadingIcon={Fuel} onClick={onContinue}>
            {t('verify.continue')}
          </Button>
          <Button variant="ghost" size="lg" fullWidth onClick={onCancel}>
            {t('common:actions.cancel')}
          </Button>
        </>
      }
    >
      {check.known ? (
        <>
          <StatusChip tone="success" size="md" icon={CircleCheck} label={t('verify.title')} />
          <Plate value={check.plate} size="lg" />
          <p className="text-lg font-medium">{locale === 'ar' ? check.companyNameAr : check.companyNameEn}</p>
          <div className="flex flex-wrap justify-center gap-2">
            <StatusChip tone="neutral" size="md" icon={Fuel} label={fuelName(check.fuelType)} />
            <StatusChip tone={WALLET_TONE[check.walletStatus]} size="md" label={t(`live.wallet.${check.walletStatus}`)} />
            {!check.billable && <StatusChip tone="brand" size="md" label={t('success.nonBillable')} />}
          </div>
          <dl className="w-full divide-y divide-border rounded-container border border-border bg-surface px-4 text-start">
            {check.billable &&
              (check.limits.length > 0 ? (
                check.limits.map((limit) => (
                  <Fact key={`${limit.kind}-${limit.period}`} label={t(`live.verify.limit.${limit.period}`)} tone={limit.remaining === 0 ? 'danger' : undefined}>
                    {limitText(limit)}
                  </Fact>
                ))
              ) : (
                <Fact label={t('live.verify.limit.DAY')}>{t('verify.unlimited')}</Fact>
              ))}
            <Fact label={t('live.verify.cooldown')}>{t('live.verify.noCooldown')}</Fact>
            {check.unitPricePiastres !== undefined && (
              <Fact label={t('live.verify.price')}>
                <Money value={check.unitPricePiastres} />
              </Fact>
            )}
          </dl>
          {check.walletStatus === 'INSUFFICIENT' && online && <Banner tone="warning" title={t('live.verify.walletInsufficient')} />}
        </>
      ) : (
        <>
          <StatusChip tone="info" size="md" label={t('live.verify.serverChecks')} />
          <p className="text-lg font-medium">{t('live.verify.notOnTerminal')}</p>
          <p className="text-sm text-muted">{t(`live.verify.unknown.${check.reason}`)}</p>
        </>
      )}
    </Screen>
  );
}

type EntryMode = 'litres' | 'amount';
const MAX_DIGITS = 7;

function VolumeScreen({
  read,
  pumpFuel,
  stationFuels,
  onPumpFuel,
  onReview,
  onBack,
}: {
  read: TagRead;
  pumpFuel?: FuelType;
  stationFuels: readonly FuelType[];
  onPumpFuel: (fuel: FuelType) => void;
  onReview: (volumeMl: number) => void;
  onBack: () => void;
}) {
  const { t } = useTranslation(['pos', 'common']);
  const { locale } = useLocale();
  const fuelName = useFuelName();
  const limitText = useLimitText();
  const { check } = read;
  const price = check.known ? check.unitPricePiastres : undefined;
  const [mode, setMode] = useState<EntryMode>('litres');
  const [picking, setPicking] = useState(false);
  const [entry, setEntry] = useState('');

  const parsed = parseDecimal(entry || '0', mode === 'litres' ? 3 : 2) ?? 0;
  const volumeMl = mode === 'litres' ? parsed : price ? volumeForAmount(parsed, price) : 0;
  const preview = quote(check, volumeMl);
  const valid = volumeMl > 0 && volumeMl <= MAX_VOLUME_ML;

  const addDigit = (digit: string) =>
    setEntry((value) => {
      const [, decimals = ''] = value.split('.');
      if (value.includes('.') && decimals.length >= 2) return value;
      if (value.replace('.', '').length >= MAX_DIGITS) return value;
      return value === '0' ? digit : value + digit;
    });

  return (
    <Screen
      className="gap-3 py-3"
      actions={
        <>
          <Button size="pos" fullWidth disabled={!valid} onClick={() => onReview(volumeMl)}>
            {t('fuel.review')}
          </Button>
          <Button variant="ghost" size="lg" fullWidth onClick={onBack}>
            {t('common:actions.cancel')}
          </Button>
        </>
      }
    >
      <div className="flex w-full items-center justify-between gap-2">
        {check.known ? <Plate value={check.plate} size="md" /> : <span className="text-sm text-muted">{t('live.verify.serverChecks')}</span>}
        {check.known &&
          (stationFuels.length > 1 ? (
            <button
              type="button"
              onClick={() => setPicking(true)}
              aria-label={t('live.volume.changePumpFuel', { fuel: fuelName(pumpFuel ?? check.fuelType) })}
              className="flex min-h-11 items-center gap-1.5 rounded-full border border-border-strong bg-surface ps-3 pe-2 text-sm font-medium transition-colors hover:bg-surface-hover"
            >
              <Icon icon={Fuel} size={16} />
              {fuelName(pumpFuel ?? check.fuelType)}
              <Icon icon={ChevronDown} size={16} className="text-muted" />
            </button>
          ) : (
            <StatusChip tone="neutral" size="md" icon={Fuel} label={fuelName(check.fuelType)} />
          ))}
      </div>
      {picking && check.known && (
        <PosMenu title={t('live.volume.pumpFuel')} onClose={() => setPicking(false)}>
          <p className="text-sm text-muted">{t('live.volume.pumpFuelHint', { fuel: fuelName(check.fuelType) })}</p>
          <div className="grid grid-cols-1 gap-2">
            {stationFuels.map((fuel) => (
              <Button
                key={fuel}
                variant={fuel === (pumpFuel ?? check.fuelType) ? 'primary' : 'secondary'}
                size="lg"
                fullWidth
                onClick={() => {
                  setPicking(false);
                  onPumpFuel(fuel);
                }}
              >
                {fuelName(fuel)}
              </Button>
            ))}
          </div>
        </PosMenu>
      )}
      {price !== undefined && (
        <SegmentedControl<EntryMode>
          label={t('fuel.mode')}
          value={mode}
          onValueChange={(next) => {
            setMode(next);
            setEntry('');
          }}
          options={[
            { value: 'litres', label: t('fuel.liters') },
            { value: 'amount', label: t('fuel.amount') },
          ]}
          className="w-full"
        />
      )}
      <div className="flex flex-col items-center gap-0.5" aria-live="polite">
        {mode === 'litres' ? (
          <Volume value={volumeMl} className="text-(length:--pos-value-size,2.5rem) leading-tight font-semibold" />
        ) : (
          <Money value={preview.amountPiastres ?? 0} className="text-(length:--pos-value-size,2.5rem) leading-tight font-semibold" />
        )}
        {price !== undefined && (
          <span className="text-base text-muted">
            {mode === 'litres' ? <Money value={preview.amountPiastres ?? 0} /> : <Volume value={volumeMl} />}
            {' · '}
            {t('fuel.perLiter', { price: formatMoney(price, locale) })}
          </span>
        )}
        {preview.overLimit ? (
          <p className="flex items-center gap-1.5 text-sm font-medium text-danger">
            <Icon icon={TriangleAlert} size={16} />
            {t('live.volume.overLimit', { left: limitText(preview.overLimit) })}
          </p>
        ) : preview.overTank ? (
          <p className="flex items-center gap-1.5 text-sm font-medium text-warning">
            <Icon icon={TriangleAlert} size={16} />
            {t('live.volume.overTank', { tank: formatVolume(check.known ? check.tankCapacityMl : 0, locale, 0) })}
          </p>
        ) : check.known && check.limits[0] ? (
          <p className="text-sm text-muted">{limitText(check.limits[0])}</p>
        ) : null}
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

/** Litres and amount before anything is recorded, with a short guard against an accidental tap. */
function ConfirmSheet({
  read,
  volumeMl,
  pumpFuel,
  online,
  onConfirm,
  onBack,
}: {
  read: TagRead;
  volumeMl: number;
  pumpFuel?: FuelType;
  online: boolean;
  onConfirm: () => void;
  onBack: () => void;
}) {
  const { t } = useTranslation('pos');
  const { locale } = useLocale();
  const fuelName = useFuelName();
  const limitText = useLimitText();
  const [armed, setArmed] = useState(false);
  const preview = quote(read.check, volumeMl);
  const { check } = read;
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
        className="mt-auto flex flex-col gap-4 rounded-t-dialog bg-surface-elevated p-5 text-center shadow-dialog animate-[zood-sheet-from-bottom_240ms_cubic-bezier(0.22,1,0.36,1)]"
      >
        <p className="text-base font-semibold text-muted">{t('confirm.title')}</p>
        <p className="text-3xl font-semibold tabular-nums">
          {!preview.billable
            ? t('confirm.vip')
            : preview.amountPiastres !== undefined
              ? t('confirm.deduct', { amount: formatMoney(preview.amountPiastres, locale) })
              : t('live.confirm.serverPrices')}
        </p>
        <dl className="grid grid-cols-2 gap-x-4 gap-y-2 rounded-container border border-border bg-surface p-4 text-start">
          <dt className="text-muted">{t('fuel.liters')}</dt>
          <dd className="text-end text-lg font-semibold">
            <Volume value={volumeMl} />
          </dd>
          {check.known && (
            <>
              <dt className="text-muted">{t('live.confirm.vehicle')}</dt>
              <dd className="flex justify-end">
                <Plate value={check.plate} variant="plain" />
              </dd>
              <dt className="text-muted">{t('live.volume.pumpFuel')}</dt>
              <dd className="text-end font-medium">{fuelName(pumpFuel ?? check.fuelType)}</dd>
            </>
          )}
          {preview.unitPricePiastres !== undefined && (
            <>
              <dt className="text-muted">{t('live.verify.price')}</dt>
              <dd className="text-end">
                <Money value={preview.unitPricePiastres} />
              </dd>
            </>
          )}
        </dl>
        {preview.overLimit && <Banner tone="danger" title={t('live.volume.overLimit', { left: limitText(preview.overLimit) })} />}
        {!online && <Banner tone="warning" title={t('live.confirm.offline')} />}
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
          <span className="relative">{t('live.confirm.record')}</span>
        </button>
        <Button variant="ghost" size="lg" onClick={onBack}>
          {t('confirm.back')}
        </Button>
      </div>
    </div>
  );
}

function FailedScreen({ error, onRetry, onHome }: { error: string; onRetry: () => void; onHome: () => void }) {
  const { t } = useTranslation('pos');
  return (
    <Screen
      className="justify-center"
      actions={
        <>
          <Button size="pos" fullWidth onClick={onRetry}>
            {t('live.fueling.retry')}
          </Button>
          <Button variant="ghost" size="lg" fullWidth onClick={onHome}>
            {t('decline.back')}
          </Button>
        </>
      }
    >
      <ResultMark tone="danger" icon={CircleX} />
      <p role="alert" className="text-lg font-medium">
        {error}
      </p>
      <p className="text-sm text-muted">{t('live.fueling.retryHint')}</p>
    </Screen>
  );
}

/** A refusal in words the agent can act on, with a live countdown for a cooldown. */
function DeclinedScreen({ decline, onBack }: { decline: Decline; onBack: () => void }) {
  const { t } = useTranslation('pos');
  const fuelName = useFuelName();
  const cooldown = decline.reason === 'COOLDOWN';
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!cooldown || !decline.cooldownUntil) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [cooldown, decline.cooldownUntil]);
  const until = decline.cooldownUntil ? new Date(decline.cooldownUntil).getTime() : undefined;
  return (
    <Screen
      className="justify-center"
      actions={
        <Button size="pos" fullWidth onClick={onBack}>
          {t('decline.back')}
        </Button>
      }
    >
      <ResultMark tone={cooldown ? 'warning' : 'danger'} icon={cooldown ? Clock : CircleX} />
      <p role="alert" className="text-2xl font-semibold">
        {t(cooldown ? 'decline.cooldownTitle' : `live.decline.title.${decline.reason}`, { defaultValue: t('decline.title') })}
      </p>
      {cooldown && until !== undefined && (
        <div className="flex flex-col items-center gap-1">
          <span className="text-muted">{t('decline.availableIn')}</span>
          <span className="text-4xl font-semibold tabular-nums" dir="ltr">
            {formatCountdown(until, now)}
          </span>
          <span className="text-sm text-muted">{t('live.decline.at', { time: formatTime(new Date(until), i18n.language === 'en' ? 'en' : 'ar') })}</span>
        </div>
      )}
      <p className="text-lg text-muted">{t(`live.decline.reason.${decline.reason}`, { fuel: fuelName(decline.fuelType) })}</p>
      {decline.plate && <Plate value={decline.plate} size="md" />}
      <p className="max-w-72 text-sm text-muted">{t(`live.decline.help.${decline.reason}`, { defaultValue: '' })}</p>
      {decline.offline && <StatusChip tone="warning" size="md" icon={CloudOff} label={t('live.offline.decidedOffline')} />}
    </Screen>
  );
}

type PrintState = 'idle' | 'printing' | 'printed';

/** Copies or shares the public receipt link. */
async function shareReceipt(url: string, title: string, copied: string, failed: string) {
  try {
    if (navigator.share) {
      await navigator.share({ title, url });
      return;
    }
    await navigator.clipboard.writeText(url);
    toast(copied);
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') return;
    toast(failed, 'danger');
  }
}

function ReceiptActions({ entry, print, onPrint }: { entry: ShiftEntry; print: PrintState; onPrint: () => void }) {
  const { t } = useTranslation('pos');
  if (!entry.receiptPath) return null;
  const url = receiptUrl(entry.receiptPath);
  return (
    <div className="grid grid-cols-2 gap-2">
      <Button variant="secondary" size="lg" leadingIcon={Printer} loading={print === 'printing'} onClick={onPrint}>
        {t(print === 'printed' ? 'live.receipt.reprint' : 'success.print')}
      </Button>
      <Button
        variant="secondary"
        size="lg"
        leadingIcon={Share2}
        onClick={() => void shareReceipt(url, t('live.receipt.shareTitle', { reference: entry.reference }), t('live.receipt.copied'), t('live.receipt.copyFailed'))}
      >
        {t('live.receipt.share')}
      </Button>
    </div>
  );
}

function usePrint() {
  const [print, setPrint] = useState<PrintState>('idle');
  useEffect(() => {
    if (print !== 'printing') return;
    const timer = setTimeout(() => setPrint('printed'), PRINT_MS);
    return () => clearTimeout(timer);
  }, [print]);
  return [print, () => setPrint('printing')] as const;
}

/** The success moment, then the receipt: print (simulated), share, or open the public link. */
function DoneScreen({ entry, walletStatus, pending, onFinish }: { entry: ShiftEntry; walletStatus?: WalletWord; pending: boolean; onFinish: () => void }) {
  const { t } = useTranslation('pos');
  const [print, startPrint] = usePrint();
  const queued = entry.status === 'QUEUED';
  return (
    <Screen
      actions={
        <>
          <ReceiptActions entry={entry} print={print} onPrint={startPrint} />
          <Button size="pos" fullWidth onClick={onFinish}>
            {t('success.finish')}
          </Button>
        </>
      }
    >
      {print === 'idle' ? (
        <>
          <ResultMark tone="success" icon={CircleCheck} />
          <p role="status" className="text-2xl font-semibold">
            {t(queued ? 'success.offlineTitle' : 'success.title')}
          </p>
          <p className="text-4xl font-semibold tabular-nums">
            {entry.billable ? <Money value={entry.amountPiastres} /> : <span className="text-2xl">{t('success.nonBillable')}</span>}
          </p>
          <p className="text-xl">
            <Volume value={entry.volumeMl} />
          </p>
          <Plate value={entry.plate} size="lg" />
          <div className="flex flex-wrap justify-center gap-2">
            {queued && (
              <StatusChip size="md" tone={pending ? 'warning' : 'success'} icon={pending ? CloudOff : CircleCheck} label={t(pending ? 'success.pendingSync' : 'live.history.sent')} />
            )}
            {walletStatus && <StatusChip tone={WALLET_TONE[walletStatus]} size="md" label={t(`live.wallet.${walletStatus}`)} />}
          </div>
          <p className="text-sm text-muted">
            {t('live.receipt.reference')} <bdi dir="ltr" className="font-mono">{entry.reference}</bdi>
          </p>
          {queued && <p className="text-sm text-muted">{t('live.offline.savedHint')}</p>}
        </>
      ) : (
        <>
          <p role="status" className="flex items-center gap-2 text-base font-semibold">
            <Icon icon={print === 'printing' ? LoaderCircle : CircleCheck} size={18} className={print === 'printing' ? 'animate-spin' : 'text-success'} />
            {t(print === 'printing' ? 'success.printing' : 'success.printed')}
          </p>
          <ThermalReceipt model={receiptOf(entry)} printing={print === 'printing'} />
        </>
      )}
    </Screen>
  );
}

function ReceiptScreen({ entry, onBack }: { entry: ShiftEntry; onBack: () => void }) {
  const { t } = useTranslation(['pos', 'common', 'receipt']);
  const [print, startPrint] = usePrint();
  return (
    <Screen
      className="bg-surface-sunken"
      actions={
        <>
          <ReceiptActions entry={entry} print={print} onPrint={startPrint} />
          <Button variant="ghost" size="lg" fullWidth onClick={onBack}>
            {t('common:actions.back')}
          </Button>
        </>
      }
    >
      <p className="text-base font-semibold">{t('receipt:title')}</p>
      {entry.receiptPath ? (
        <ThermalReceipt model={receiptOf(entry)} printing={print === 'printing'} />
      ) : (
        <Banner tone="info" title={t('live.receipt.afterSync')} />
      )}
    </Screen>
  );
}

function useStatusOf(pendingIds: ReadonlySet<string>) {
  const { t } = useTranslation(['pos', 'live']);
  return (entry: ShiftEntry): { tone: Tone; label: string } => {
    if (entry.status === 'DECLINED') return { tone: 'danger', label: t('live.history.declined') };
    if (entry.status === 'QUEUED') return pendingIds.has(entry.id) ? { tone: 'warning', label: t('success.pendingSync') } : { tone: 'success', label: t('live.history.sent') };
    return { tone: 'success', label: t('live.history.completed') };
  };
}

function HistoryScreen({
  entries,
  pendingIds,
  onOpen,
  onSummary,
  onBack,
}: {
  entries: readonly ShiftEntry[];
  pendingIds: ReadonlySet<string>;
  onOpen: (entry: ShiftEntry) => void;
  onSummary: () => void;
  onBack: () => void;
}) {
  const { t } = useTranslation(['pos', 'common']);
  const { locale } = useLocale();
  const fuelName = useFuelName();
  const statusOf = useStatusOf(pendingIds);
  const newest = [...entries].reverse();
  return (
    <Screen
      className="items-stretch gap-3 px-0 py-0 text-start"
      actions={
        <div className="grid grid-cols-2 gap-2">
          <Button variant="secondary" size="lg" onClick={onBack}>
            {t('common:actions.back')}
          </Button>
          <Button size="lg" leadingIcon={Power} onClick={onSummary}>
            {t('endShift')}
          </Button>
        </div>
      }
    >
      <h1 className="px-5 pt-5 text-xl font-semibold">{t('history.title')}</h1>
      {newest.length === 0 ? (
        <div className="flex flex-1 flex-col items-center justify-center gap-3 px-6 text-center">
          <ResultMark tone="brand" icon={History} size="md" />
          <p className="font-medium">{t('history.empty')}</p>
          <p className="text-sm text-muted">{t('live.history.emptyHint')}</p>
        </div>
      ) : (
        <ul className="divide-y divide-border border-y border-border bg-surface">
          {newest.map((entry) => {
            const status = statusOf(entry);
            const open = entry.status !== 'DECLINED';
            return (
              <li key={entry.id}>
                <button
                  type="button"
                  disabled={!open}
                  onClick={() => onOpen(entry)}
                  className="flex min-h-16 w-full items-center gap-3 px-5 py-3 text-start transition-colors enabled:hover:bg-surface-hover disabled:cursor-default"
                >
                  <div className="min-w-0 flex-1">
                    <Plate value={entry.plate} size="sm" />
                    <p className="mt-1 truncate text-xs text-muted">
                      {formatTime(entry.at, locale)} · {fuelName(entry.fuelType)} · <bdi dir="ltr">{entry.reference}</bdi>
                    </p>
                  </div>
                  <div className="flex flex-col items-end gap-1">
                    {entry.status !== 'DECLINED' && (
                      <span className="text-sm font-semibold tabular-nums">
                        <Volume value={entry.volumeMl} />
                      </span>
                    )}
                    <StatusChip tone={status.tone} label={entry.status === 'DECLINED' && entry.declineReason ? t(`live.decline.short.${entry.declineReason}`) : status.label} />
                  </div>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </Screen>
  );
}

/** The shift in numbers before the agent signs out: what the agent hands over. */
function ShiftSummaryScreen({ entries, pendingIds, onEnd, onBack }: { entries: readonly ShiftEntry[]; pendingIds: ReadonlySet<string>; onEnd: () => void; onBack: () => void }) {
  const { t } = useTranslation(['pos', 'common']);
  const { locale } = useLocale();
  const fuelName = useFuelName();
  const summary = summariseShift(entries, pendingIds);
  return (
    <Screen
      className="items-stretch gap-4 text-start"
      actions={
        <>
          <Button size="pos" fullWidth leadingIcon={Power} onClick={onEnd}>
            {t('live.summary.end')}
          </Button>
          <Button variant="ghost" size="lg" fullWidth onClick={onBack}>
            {t('live.summary.keepWorking')}
          </Button>
        </>
      }
    >
      <div>
        <h1 className="text-xl font-semibold">{t('live.summary.title')}</h1>
        <p className="text-sm text-muted">{t('live.summary.hint')}</p>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <div className="col-span-2 rounded-container border border-border bg-surface p-4">
          <p className="flex items-center gap-2 text-sm text-muted">
            <span aria-hidden className="h-1 w-4 rounded-full bg-accent" />
            {t('live.summary.amount')}
          </p>
          <p className="text-kpi font-semibold tabular-nums">
            <Money value={summary.amountPiastres} />
          </p>
          {summary.notBilledPiastres > 0 && (
            <p className="text-xs text-muted">{t('live.summary.notBilled', { amount: formatMoney(summary.notBilledPiastres, locale) })}</p>
          )}
        </div>
        <div className="rounded-container border border-border bg-surface p-4">
          <p className="text-sm text-muted">{t('live.summary.fuelings')}</p>
          <p className="text-2xl font-semibold tabular-nums">{formatNumber(summary.fuelings, locale)}</p>
        </div>
        <div className="rounded-container border border-border bg-surface p-4">
          <p className="text-sm text-muted">{t('live.summary.litres')}</p>
          <p className="text-2xl font-semibold tabular-nums">
            <Volume value={summary.volumeMl} fractionDigits={0} />
          </p>
        </div>
      </div>
      {summary.byFuel.length > 0 && (
        <section aria-label={t('live.summary.byFuel')} className="rounded-container border border-border bg-surface">
          <p className="border-b border-border px-4 py-2.5 text-sm font-semibold">{t('live.summary.byFuel')}</p>
          <ul className="divide-y divide-border">
            {summary.byFuel.map((line) => (
              <li key={line.fuelType} className="flex items-center justify-between gap-3 px-4 py-3">
                <div>
                  <p className="font-medium">{fuelName(line.fuelType)}</p>
                  <p className="text-xs text-muted">{t('live.summary.count', { count: line.fuelings })}</p>
                </div>
                <div className="text-end tabular-nums">
                  <p className="font-semibold">
                    <Money value={line.amountPiastres} />
                  </p>
                  <p className="text-xs text-muted">
                    <Volume value={line.volumeMl} />
                  </p>
                </div>
              </li>
            ))}
          </ul>
        </section>
      )}
      {summary.declined > 0 && <p className="text-sm text-muted">{t('live.summary.declined', { count: summary.declined })}</p>}
      {summary.waiting > 0 && <Banner tone="warning" title={t('live.summary.waiting', { count: summary.waiting })} description={t('live.summary.waitingHint')} />}
    </Screen>
  );
}

