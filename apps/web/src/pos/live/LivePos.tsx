/**
 * The POS against the live API (VITE_API_MODE=http). The terminal is this browser: it activates
 * once with the code Ops issued (its own key pair stays here), the agent signs in with Worker ID and
 * PIN, and each fueling is decided by the server (B4 contract). RFID is simulated: the agent types
 * or pastes the tag UID that a reader would return.
 */
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { CircleCheck, CircleX, Clock, CloudOff, LogOut, Nfc, RefreshCw, Wifi } from 'lucide-react';
import { ApiRequestError, NETWORK_ERROR } from '@/api/http-client';
import { BrandLogo, Button, Icon, Money, Numpad, Plate, StatusChip, TextField, Volume, type Tone } from '@/design-system';
import { ThermalReceipt } from '@/features/receipt/ThermalReceipt';
import type { ReceiptModel } from '@/features/receipt/model';
import { cn } from '@/lib/cn';
import type { Locale } from '@/lib/locale';
import { normalizeDigits, parseLitres } from '@/live/input';
import { i18n } from '@/i18n';
import type { OfflineDecline, QueuedCapture } from './offline';
import { useOffline, type OfflineState } from './use-offline';
import {
  activate,
  agentSignIn,
  agentSignOut,
  changePin,
  deactivateHere,
  DEVICE_ENDED,
  loadDevice,
  loadSession,
  recordFueling,
  saveSession,
  SESSION_ENDED,
  type AgentSession,
  type DeclineReason,
  type DeviceIdentity,
  type PosFueling,
  type WalletStatus,
} from './pos-api';

const PIN_LENGTH = 6;

/** The sentence for a refusal, from its code; never the server's English message. */
function useErrorText() {
  const { t } = useTranslation('pos');
  return (error: unknown): string => {
    const code = error instanceof ApiRequestError ? error.code : 'UNKNOWN';
    return t(`live.errors.${code}`, { defaultValue: t('live.errors.UNKNOWN') });
  };
}

function Screen({ children, actions, className }: { children: ReactNode; actions?: ReactNode; className?: string }) {
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className={cn('flex min-h-0 flex-1 flex-col items-center gap-5 overflow-y-auto px-5 py-6 text-center', className)}>
        {children}
      </div>
      {actions && <div className="flex flex-col gap-2 px-4 pt-2 pb-4">{actions}</div>}
    </div>
  );
}

function ErrorLine({ text }: { text: string | undefined }) {
  return (
    <p role="alert" className="min-h-5 text-sm font-medium text-danger">
      {text ?? ''}
    </p>
  );
}

export function LivePos() {
  const [device, setDevice] = useState<DeviceIdentity | undefined>(loadDevice);
  const [session, setSession] = useState<AgentSession | undefined>(loadSession);
  const [notice, setNotice] = useState<string>();

  const deviceEnded = () => {
    void deactivateHere();
    setSession(undefined);
    setDevice(undefined);
    setNotice('deviceEnded');
  };
  const sessionEnded = () => {
    saveSession(undefined);
    setSession(undefined);
    setNotice('sessionEnded');
  };

  if (!device) {
    return (
      <ActivateScreen
        notice={notice}
        onActivated={(next) => {
          setNotice(undefined);
          setDevice(next);
        }}
      />
    );
  }
  return (
    <DeviceShell
      key={device.deviceId}
      device={device}
      session={session}
      notice={notice}
      onSignedIn={(next) => {
        setNotice(undefined);
        setSession(next);
      }}
      onPinChanged={setSession}
      onSessionEnded={sessionEnded}
      onDeviceEnded={deviceEnded}
      onSignOut={() => {
        if (session) void agentSignOut(device, session).catch(() => undefined);
        setSession(undefined);
      }}
    />
  );
}

/**
 * An activated terminal. Its offline state lives here, not with the agent, so the queue keeps being
 * sent after the shift ends (B5 contract §3).
 */
function DeviceShell({
  device,
  session,
  notice,
  onSignedIn,
  onPinChanged,
  onSessionEnded,
  onDeviceEnded,
  onSignOut,
}: {
  device: DeviceIdentity;
  session: AgentSession | undefined;
  notice?: string;
  onSignedIn: (session: AgentSession) => void;
  onPinChanged: (session: AgentSession) => void;
  onSessionEnded: () => void;
  onDeviceEnded: () => void;
  onSignOut: () => void;
}) {
  const offline = useOffline(device, session && !session.agent.mustChangePin ? session : undefined);
  if (!session) {
    return (
      <>
        <AgentLoginScreen device={device} notice={notice} onDeviceEnded={onDeviceEnded} onSignedIn={onSignedIn} />
        {(offline.queue.length > 0 || offline.rejectedCount > 0) && (
          <div className="px-6 pb-4">
            <SyncPanel state={offline} />
          </div>
        )}
      </>
    );
  }
  if (session.agent.mustChangePin) {
    return <PinChangeScreen device={device} session={session} onChanged={onPinChanged} onSessionEnded={onSessionEnded} />;
  }
  return (
    <FuelFlow
      device={device}
      session={session}
      offline={offline}
      onSessionEnded={onSessionEnded}
      onDeviceEnded={onDeviceEnded}
      onSignOut={onSignOut}
    />
  );
}

// ---------------------------------------------------------------------------------- activation

function ActivateScreen({ notice, onActivated }: { notice?: string; onActivated: (device: DeviceIdentity) => void }) {
  const { t } = useTranslation('pos');
  const errorText = useErrorText();
  const [deviceCode, setDeviceCode] = useState('');
  const [activationCode, setActivationCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const submit = async () => {
    setBusy(true);
    setError(undefined);
    try {
      onActivated(await activate(deviceCode.toUpperCase(), activationCode.toUpperCase()));
    } catch (cause) {
      setError(errorText(cause));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Screen
      actions={
        <Button size="pos" fullWidth loading={busy} disabled={!deviceCode.trim() || !activationCode.trim()} onClick={() => void submit()}>
          {t('live.activate.submit')}
        </Button>
      }
    >
      <BrandLogo size="md" />
      <h1 className="text-xl font-semibold">{t('live.activate.title')}</h1>
      <p className="text-sm text-muted">{t(notice === 'deviceEnded' ? 'live.activate.ended' : 'live.activate.hint')}</p>
      <div className="flex w-full flex-col gap-4 text-start">
        <TextField
          label={t('live.activate.deviceCode')}
          value={deviceCode}
          onChange={(event) => setDeviceCode(event.target.value)}
          dir="ltr"
          autoCapitalize="characters"
          autoComplete="off"
          spellCheck={false}
        />
        <TextField
          label={t('live.activate.activationCode')}
          value={activationCode}
          onChange={(event) => setActivationCode(event.target.value)}
          dir="ltr"
          autoCapitalize="characters"
          autoComplete="one-time-code"
          spellCheck={false}
        />
      </div>
      <ErrorLine text={error} />
    </Screen>
  );
}

// ---------------------------------------------------------------------------------------- PIN

function PinPad({ label, onComplete, error, busy }: { label: string; onComplete: (pin: string) => void; error?: string; busy?: boolean }) {
  const [pin, setPin] = useState('');
  const add = (digit: string) => {
    if (busy || pin.length >= PIN_LENGTH) return;
    const next = pin + digit;
    setPin(next);
    if (next.length === PIN_LENGTH) {
      setPin('');
      onComplete(next);
    }
  };
  return (
    <div className="flex w-full flex-col items-center gap-3">
      <span className="text-sm font-medium">{label}</span>
      <div dir="ltr" className="flex gap-3" aria-hidden>
        {Array.from({ length: PIN_LENGTH }, (_, index) => (
          <span
            key={index}
            className={cn('size-3.5 rounded-full border-2', index < pin.length ? 'border-primary bg-primary' : 'border-border-strong')}
          />
        ))}
      </div>
      <ErrorLine text={error} />
      <Numpad className="w-full" onDigit={add} onBackspace={() => setPin((value) => value.slice(0, -1))} />
    </div>
  );
}

function AgentLoginScreen({
  device,
  notice,
  onSignedIn,
  onDeviceEnded,
}: {
  device: DeviceIdentity;
  notice?: string;
  onSignedIn: (session: AgentSession) => void;
  onDeviceEnded: () => void;
}) {
  const { t } = useTranslation('pos');
  const errorText = useErrorText();
  const [workerId, setWorkerId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const submit = async (pin: string) => {
    setBusy(true);
    setError(undefined);
    try {
      onSignedIn(await agentSignIn(device, workerId.toUpperCase(), pin));
    } catch (cause) {
      if (cause instanceof ApiRequestError && DEVICE_ENDED.has(cause.code)) {
        onDeviceEnded();
        return;
      }
      setError(errorText(cause));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-1 flex-col gap-5 overflow-y-auto px-6 py-6">
      <div className="flex flex-col items-center gap-2 pt-2">
        <BrandLogo size="md" />
        <h1 className="text-xl font-semibold">{t('login.title')}</h1>
        <p className="text-sm text-muted">
          {notice === 'sessionEnded' ? t('live.login.sessionEnded') : t('live.login.device', { code: device.deviceCode })}
        </p>
      </div>
      <TextField
        label={t('login.workerIdLabel')}
        value={workerId}
        onChange={(event) => setWorkerId(normalizeDigits(event.target.value))}
        dir="ltr"
        autoCapitalize="characters"
        autoComplete="username"
        spellCheck={false}
        className="text-start"
      />
      <PinPad label={t('login.pin')} busy={busy || !workerId.trim()} error={error} onComplete={(pin) => void submit(pin)} />
    </div>
  );
}

function PinChangeScreen({
  device,
  session,
  onChanged,
  onSessionEnded,
}: {
  device: DeviceIdentity;
  session: AgentSession;
  onChanged: (session: AgentSession) => void;
  onSessionEnded: () => void;
}) {
  const { t } = useTranslation('pos');
  const errorText = useErrorText();
  const [step, setStep] = useState<{ stage: 'current' } | { stage: 'new'; current: string } | { stage: 'repeat'; current: string; next: string }>({
    stage: 'current',
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const complete = async (pin: string) => {
    setError(undefined);
    if (step.stage === 'current') return setStep({ stage: 'new', current: pin });
    if (step.stage === 'new') return setStep({ stage: 'repeat', current: step.current, next: pin });
    if (pin !== step.next) {
      setError(t('live.pin.mismatch'));
      return setStep({ stage: 'new', current: step.current });
    }
    setBusy(true);
    try {
      onChanged(await changePin(device, session, step.current, pin));
    } catch (cause) {
      if (cause instanceof ApiRequestError && SESSION_ENDED.has(cause.code)) return onSessionEnded();
      setError(errorText(cause));
      if (cause instanceof ApiRequestError && cause.field === 'currentPin') setStep({ stage: 'current' });
      else setStep({ stage: 'new', current: step.current });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-1 flex-col gap-5 overflow-y-auto px-6 py-6">
      <div className="flex flex-col items-center gap-2 pt-2 text-center">
        <h1 className="text-xl font-semibold">{t('live.pin.title')}</h1>
        <p className="text-sm text-muted">{t('live.pin.hint')}</p>
      </div>
      <PinPad key={step.stage} label={t(`live.pin.${step.stage}`)} busy={busy} error={error} onComplete={(pin) => void complete(pin)} />
    </div>
  );
}

// ------------------------------------------------------------------------------------ fueling

type FuelStep =
  | { name: 'home' }
  | { name: 'tag' }
  | { name: 'volume'; tagUid: string }
  | { name: 'sending'; tagUid: string; litres: string; clientTxnId: string }
  | { name: 'failed'; tagUid: string; litres: string; clientTxnId: string; error: string }
  | { name: 'result'; fueling: PosFueling }
  | { name: 'unknownTag' }
  | { name: 'offlineDone'; capture: QueuedCapture }
  | { name: 'offlineDeclined'; reason: OfflineDecline; plate?: string };

function FuelFlow({
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
  const errorText = useErrorText();
  const [step, setStep] = useState<FuelStep>({ name: 'home' });
  const receiptLocale = (i18n.language === 'en' ? 'en' : 'ar') as Locale;

  /**
   * Decides on the terminal against its snapshot and queues the capture (B5 contract §2). `fromAttempt`
   * is set when an online attempt got no answer: what the terminal decides is then recorded with it.
   */
  const captureOffline = async (tagUid: string, litres: string, clientTxnId: string, volumeMl: number, fromAttempt = false) => {
    // An agent whose session has ended (expired while offline) must sign in again, which needs the network.
    if (new Date(session.expiresAt).getTime() <= offline.serverNow().getTime()) return onSessionEnded();
    try {
      const outcome = await offline.captureOffline({ clientTxnId, tagUid, volumeMl, agentId: session.agent.id, receiptLocale });
      if ('noSnapshot' in outcome) {
        if (fromAttempt) await offline.settleAttempt(clientTxnId, 'REFUSED');
        setStep({ name: 'failed', tagUid, litres, clientTxnId, error: t('live.offline.noSnapshot') });
      } else if (!outcome.decision.ok) {
        if (fromAttempt) await offline.settleAttempt(clientTxnId, 'REFUSED');
        setStep({ name: 'offlineDeclined', reason: outcome.decision.reason, plate: outcome.decision.plate });
      } else if (outcome.capture) {
        if (fromAttempt) await offline.settleAttempt(clientTxnId, 'QUEUED');
        setStep({ name: 'offlineDone', capture: outcome.capture });
      }
    } catch (cause) {
      // The capture could not be saved on the terminal: say so; never claim it is done.
      setStep({ name: 'failed', tagUid, litres, clientTxnId, error: errorText(cause) });
    }
  };

  /**
   * Sends the fueling. A retry after a lost answer reuses the same clientTxnId, so it is charged
   * once; with no answer at all the capture falls back to the offline queue under that same id, and
   * if the first request did reach the server the sync answers DUPLICATE.
   */
  const send = async (tagUid: string, litres: string, clientTxnId: string) => {
    const volumeMl = parseLitres(litres);
    if (volumeMl === null) return;
    setStep({ name: 'sending', tagUid, litres, clientTxnId });
    if (!offline.online) return captureOffline(tagUid, litres, clientTxnId, volumeMl);
    // Remembered before it is sent: if the answer is lost, or the page reloads, the terminal still
    // knows to ask the server later what it did (B5 review 6).
    await offline.beginAttempt({ clientTxnId, tagUid, volumeMl });
    try {
      const fueling = await recordFueling(device, session, {
        clientTxnId,
        tagUid,
        volumeMl,
        deviceAt: offline.serverNow().toISOString(),
        receiptLocale,
      });
      await offline.settleAttempt(clientTxnId, 'ANSWERED');
      setStep({ name: 'result', fueling });
    } catch (cause) {
      if (cause instanceof ApiRequestError && cause.code === NETWORK_ERROR) {
        offline.noteFailure(cause);
        return captureOffline(tagUid, litres, clientTxnId, volumeMl, true);
      }
      await offline.settleAttempt(clientTxnId, 'ANSWERED');
      if (cause instanceof ApiRequestError) {
        if (DEVICE_ENDED.has(cause.code)) return onDeviceEnded();
        if (SESSION_ENDED.has(cause.code)) return onSessionEnded();
        if (cause.code === 'TAG_NOT_RECOGNISED') return setStep({ name: 'unknownTag' });
      }
      setStep({ name: 'failed', tagUid, litres, clientTxnId, error: errorText(cause) });
    }
  };

  switch (step.name) {
    case 'home':
      return (
        <Screen
          className="justify-center"
          actions={
            <Button variant="secondary" size="lg" fullWidth leadingIcon={LogOut} onClick={onSignOut}>
              {t('endShift')}
            </Button>
          }
        >
          <BrandLogo size="md" />
          <p className="text-sm text-muted">
            {session.agent.fullName} · <bdi dir="ltr">{session.agent.workerId}</bdi>
          </p>
          <Button size="pos" fullWidth leadingIcon={Nfc} onClick={() => setStep({ name: 'tag' })}>
            {t('scan')}
          </Button>
          <SyncPanel state={offline} />
        </Screen>
      );
    case 'tag':
      return <TagScreen onRead={(tagUid) => setStep({ name: 'volume', tagUid })} onCancel={() => setStep({ name: 'home' })} />;
    case 'volume':
      return (
        <VolumeScreen
          onSubmit={(litres) => void send(step.tagUid, litres, crypto.randomUUID())}
          onBack={() => setStep({ name: 'home' })}
        />
      );
    case 'sending':
      return (
        <Screen className="justify-center">
          <span className="size-12 animate-spin rounded-full border-4 border-border border-t-primary" aria-hidden />
          <p className="text-lg font-medium">{t('processing.online')}</p>
        </Screen>
      );
    case 'failed':
      return (
        <Screen
          className="justify-center"
          actions={
            <>
              <Button size="pos" fullWidth onClick={() => void send(step.tagUid, step.litres, step.clientTxnId)}>
                {t('live.fueling.retry')}
              </Button>
              <Button variant="secondary" size="lg" fullWidth onClick={() => setStep({ name: 'home' })}>
                {t('decline.back')}
              </Button>
            </>
          }
        >
          <span className="flex size-20 items-center justify-center rounded-full bg-danger-subtle text-danger">
            <Icon icon={CircleX} size={44} />
          </span>
          <p role="alert" className="text-lg font-medium">
            {step.error}
          </p>
          <p className="text-sm text-muted">{t('live.fueling.retryHint')}</p>
        </Screen>
      );
    case 'unknownTag':
      return (
        <Screen
          className="justify-center"
          actions={
            <Button size="pos" fullWidth onClick={() => setStep({ name: 'home' })}>
              {t('decline.back')}
            </Button>
          }
        >
          <span className="flex size-20 items-center justify-center rounded-full bg-danger-subtle text-danger">
            <Icon icon={CircleX} size={44} />
          </span>
          <p role="alert" className="text-2xl font-semibold">
            {t('decline.title')}
          </p>
          <p className="text-lg text-muted">{t('decline.reasons.unknownTag')}</p>
        </Screen>
      );
    case 'result':
      return <ResultScreen fueling={step.fueling} onFinish={() => setStep({ name: 'home' })} />;
    case 'offlineDone':
      return <OfflineDoneScreen capture={step.capture} onFinish={() => setStep({ name: 'home' })} />;
    case 'offlineDeclined':
      return <OfflineDeclinedScreen reason={step.reason} plate={step.plate} onBack={() => setStep({ name: 'home' })} />;
  }
}

function TagScreen({ onRead, onCancel }: { onRead: (uid: string) => void; onCancel: () => void }) {
  const { t } = useTranslation('pos');
  const [uid, setUid] = useState('');
  const clean = uid.replace(/[\s:-]/g, '').toUpperCase();
  const valid = /^[0-9A-F]{8,32}$/.test(clean);
  return (
    <Screen
      actions={
        <>
          <Button size="pos" fullWidth disabled={!valid} onClick={() => onRead(clean)}>
            {t('live.tag.read')}
          </Button>
          <Button variant="secondary" size="lg" fullWidth onClick={onCancel}>
            {t('decline.back')}
          </Button>
        </>
      }
    >
      <span className="flex size-20 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
        <Icon icon={Nfc} size={44} />
      </span>
      <h1 className="text-xl font-semibold">{t('live.tag.title')}</h1>
      <p className="text-sm text-muted">{t('live.tag.hint')}</p>
      <TextField
        label={t('live.tag.uid')}
        value={uid}
        onChange={(event) => setUid(event.target.value)}
        dir="ltr"
        autoCapitalize="characters"
        autoComplete="off"
        spellCheck={false}
        className="w-full text-start"
      />
    </Screen>
  );
}

function VolumeScreen({ onSubmit, onBack }: { onSubmit: (litres: string) => void; onBack: () => void }) {
  const { t } = useTranslation('pos');
  const [litres, setLitres] = useState('');
  const volumeMl = parseLitres(litres);
  const add = (digit: string) => {
    const next = litres + digit;
    if (/^\d{0,4}(\.\d{0,2})?$/.test(next)) setLitres(next);
  };
  return (
    <Screen
      actions={
        <>
          <Button size="pos" fullWidth disabled={volumeMl === null || volumeMl <= 0} onClick={() => onSubmit(litres)}>
            {t('live.fueling.submit')}
          </Button>
          <Button variant="secondary" size="lg" fullWidth onClick={onBack}>
            {t('decline.back')}
          </Button>
        </>
      }
    >
      <h1 className="text-xl font-semibold">{t('fuel.title')}</h1>
      <p className="flex items-baseline gap-2" dir="ltr">
        <span className="text-(length:--pos-value-size,2.5rem) font-semibold tabular-nums">{litres || '0'}</span>
        <span className="text-muted">{t('fuel.liters')}</span>
      </p>
      <Numpad
        className="w-full"
        onDigit={add}
        onDecimal={() => !litres.includes('.') && setLitres((value) => (value || '0') + '.')}
        onBackspace={() => setLitres((value) => value.slice(0, -1))}
      />
    </Screen>
  );
}

/** The demo POS's sentences, reused; the live limit is per day, week or month, so it has its own. */
const REASON_KEY = {
  COOLDOWN: 'cooldown',
  TAG_INACTIVE: 'tagInactive',
  VEHICLE_INACTIVE: 'vehicleInactive',
  COMPANY_FROZEN: 'companyFrozen',
  FUEL_TYPE_MISMATCH: 'fuelTypeMismatch',
  FUEL_NOT_AVAILABLE: 'fuelNotAvailable',
  INSUFFICIENT_BALANCE: 'insufficientBalance',
} as const satisfies Record<Exclude<DeclineReason, 'LIMIT_EXCEEDED'>, string>;

function useReasonText() {
  const { t } = useTranslation('pos');
  return (reason: DeclineReason): string =>
    reason === 'LIMIT_EXCEEDED' ? t('live.reasons.limitExceeded') : t(`decline.reasons.${REASON_KEY[reason]}`);
}

const WALLET_TONE: Record<WalletStatus, Tone> = {
  AVAILABLE: 'success',
  LOW: 'warning',
  INSUFFICIENT: 'danger',
  SUSPENDED: 'danger',
};

/** The receipt model the thermal preview prints, from the server's answer. */
function receiptOf(fueling: PosFueling): ReceiptModel {
  const fuel = (locale: Locale) => i18n.getFixedT(locale, 'live')(`enums.fuel.${fueling.fuelType}`);
  return {
    reference: fueling.reference,
    clientTxnId: fueling.id,
    qrUrl: `${window.location.origin}${fueling.receiptPath.replace(/^\/api\/v1/, '')}`,
    stationName: { ar: fueling.stationNameAr, en: fueling.stationNameEn },
    timeZone: 'Africa/Cairo',
    at: fueling.evaluatedAt,
    plate: fueling.plate,
    fuelName: { ar: fuel('ar'), en: fuel('en') },
    volumeMl: fueling.volumeMl,
    unitPricePiastres: fueling.unitPricePiastres,
    amountPiastres: fueling.amountPiastres,
    billable: fueling.billable,
    locale: fueling.receiptLocale === 'en' ? 'en' : 'ar',
    pendingSync: false,
  };
}

function ResultScreen({ fueling, onFinish }: { fueling: PosFueling; onFinish: () => void }) {
  const { t } = useTranslation('pos');
  const reasonText = useReasonText();
  const completed = fueling.status === 'COMPLETED';
  const cooldown = fueling.declineReason === 'COOLDOWN';
  return (
    <Screen
      actions={
        <Button size="pos" fullWidth onClick={onFinish}>
          {completed ? t('success.finish') : t('decline.back')}
        </Button>
      }
    >
      <span
        className={cn(
          'flex size-20 items-center justify-center rounded-full',
          completed ? 'bg-success-subtle text-success' : cooldown ? 'bg-warning-subtle text-warning' : 'bg-danger-subtle text-danger',
        )}
      >
        <Icon icon={completed ? CircleCheck : cooldown ? Clock : CircleX} size={44} />
      </span>
      <p role="status" className="text-2xl font-semibold">
        {completed ? t('success.title') : cooldown ? t('decline.cooldownTitle') : t('decline.title')}
      </p>
      {!completed && fueling.declineReason && <p className="text-lg text-muted">{reasonText(fueling.declineReason)}</p>}
      <Plate value={fueling.plate} size="md" />
      {fueling.walletStatus && (
        <StatusChip tone={WALLET_TONE[fueling.walletStatus]} size="md" label={t(`live.wallet.${fueling.walletStatus}`)} />
      )}
      {completed && !fueling.billable && <StatusChip tone="brand" size="md" label={t('success.nonBillable')} />}
      {completed && <ThermalReceipt model={receiptOf(fueling)} />}
    </Screen>
  );
}

// ------------------------------------------------------------------------------------ offline

/** Connectivity, what waits to be sent, and the simulator's offline switch. */
function SyncPanel({ state }: { state: OfflineState }) {
  const { t } = useTranslation('pos');
  const errorText = useErrorText();
  const pending = state.queue.length;
  return (
    <div className="flex w-full flex-col items-center gap-3 rounded-container border border-border bg-surface p-4">
      <div className="flex flex-wrap items-center justify-center gap-2">
        <StatusChip
          tone={state.online ? 'success' : 'warning'}
          size="md"
          icon={state.online ? Wifi : CloudOff}
          label={t(state.online ? 'live.offline.online' : 'live.offline.offline')}
        />
        {pending > 0 && <StatusChip tone="info" size="md" label={t('live.offline.pending', { count: pending })} />}
      </div>
      {!state.snapshot && <p className="text-sm text-muted">{t('live.offline.noSnapshot')}</p>}
      {state.rejectedCount > 0 && (
        <p role="alert" className="text-sm font-medium text-danger">
          {t('live.offline.rejected', { count: state.rejectedCount })}
        </p>
      )}
      {state.resolvedCount > 0 && <p className="text-sm text-muted">{t('live.offline.resolved', { count: state.resolvedCount })}</p>}
      {state.lastError && (
        <p role="alert" className="text-sm text-danger">
          {t('live.offline.syncFailed', { reason: errorText(new ApiRequestError({ status: 0, code: state.lastError, message: '', retryable: true })) })}
        </p>
      )}
      {state.lastSync && (
        <p className="text-sm text-muted">
          {t('live.offline.lastSync', {
            recorded: state.lastSync.recorded,
            duplicates: state.lastSync.duplicates,
            rejected: state.lastSync.rejected,
          })}
        </p>
      )}
      <div className="flex w-full flex-wrap justify-center gap-2">
        {state.online && pending > 0 && (
          <Button variant="secondary" leadingIcon={RefreshCw} loading={state.syncing} onClick={() => void state.sync()}>
            {t('live.offline.syncNow')}
          </Button>
        )}
        {(pending > 0 || state.rejectedCount > 0) && (
          <Button variant="ghost" onClick={() => void state.exportQueue()}>
            {t('live.offline.export')}
          </Button>
        )}
        <Button variant="ghost" onClick={() => state.setForcedOffline(!state.forcedOffline)}>
          {t(state.forcedOffline ? 'live.offline.simulateOnline' : 'live.offline.simulateOffline')}
        </Button>
      </div>
    </div>
  );
}

function OfflineDoneScreen({ capture, onFinish }: { capture: QueuedCapture; onFinish: () => void }) {
  const { t } = useTranslation('pos');
  return (
    <Screen
      actions={
        <Button size="pos" fullWidth onClick={onFinish}>
          {t('success.finish')}
        </Button>
      }
    >
      <span className="flex size-20 items-center justify-center rounded-full bg-success-subtle text-success">
        <Icon icon={CircleCheck} size={44} />
      </span>
      <p role="status" className="text-2xl font-semibold">
        {t('success.offlineTitle')}
      </p>
      <Plate value={capture.plate} size="md" />
      <StatusChip tone="warning" size="md" icon={CloudOff} label={t('success.pendingSync')} />
      <dl className="grid w-full grid-cols-2 gap-x-4 gap-y-2 rounded-container border border-border bg-surface p-4 text-start">
        <dt className="text-muted">{t('live.offline.reference')}</dt>
        <dd className="text-end font-medium">
          <bdi dir="ltr">{capture.clientTxnId.slice(0, 8).toUpperCase()}</bdi>
        </dd>
        <dt className="text-muted">{t('fuel.liters')}</dt>
        <dd className="text-end">
          <Volume value={capture.volumeMl} />
        </dd>
        <dt className="text-muted">{t('live.offline.amount')}</dt>
        <dd className="text-end font-semibold">
          {capture.billable ? <Money value={capture.amountPiastres} /> : t('success.nonBillable')}
        </dd>
      </dl>
      <p className="text-sm text-muted">{t('live.offline.savedHint')}</p>
    </Screen>
  );
}

function OfflineDeclinedScreen({ reason, plate, onBack }: { reason: OfflineDecline; plate?: string; onBack: () => void }) {
  const { t } = useTranslation('pos');
  const reasonText = useReasonText();
  const cooldown = reason === 'COOLDOWN';
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
      <p className="text-lg text-muted">
        {reason === 'UNKNOWN_TAG'
          ? t('decline.reasons.unknownTag')
          : reason === 'SNAPSHOT_STALE'
            ? t('live.offline.snapshotStale')
            : reason === 'STATION_CLOSED'
              ? t('live.offline.stationClosed')
              : reasonText(reason)}
      </p>
      {plate && <Plate value={plate} size="md" />}
      <StatusChip tone="warning" size="md" icon={CloudOff} label={t('live.offline.decidedOffline')} />
    </Screen>
  );
}
