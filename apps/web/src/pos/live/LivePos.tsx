/**
 * The POS against the live API (VITE_API_MODE=http). The terminal is this browser: it activates
 * once with the code Ops issued (its own key pair stays here), the agent signs in with Worker ID and
 * PIN, and each fueling is decided by the server (B4 contract). RFID is simulated: the agent types
 * or pastes the tag UID that a reader would return.
 */
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { CircleCheck, CircleX, Clock, LogOut, Nfc } from 'lucide-react';
import { ApiRequestError } from '@/api/http-client';
import { BrandLogo, Button, Icon, Numpad, Plate, StatusChip, TextField, type Tone } from '@/design-system';
import { ThermalReceipt } from '@/features/receipt/ThermalReceipt';
import type { ReceiptModel } from '@/features/receipt/model';
import { cn } from '@/lib/cn';
import type { Locale } from '@/lib/locale';
import { normalizeDigits, parseLitres } from '@/live/input';
import { i18n } from '@/i18n';
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
  if (!session) {
    return (
      <AgentLoginScreen
        device={device}
        notice={notice}
        onDeviceEnded={deviceEnded}
        onSignedIn={(next) => {
          setNotice(undefined);
          setSession(next);
        }}
      />
    );
  }
  if (session.agent.mustChangePin) {
    return <PinChangeScreen device={device} session={session} onChanged={setSession} onSessionEnded={sessionEnded} />;
  }
  return (
    <FuelFlow
      device={device}
      session={session}
      onSessionEnded={sessionEnded}
      onDeviceEnded={deviceEnded}
      onSignOut={() => {
        void agentSignOut(device, session).catch(() => undefined);
        setSession(undefined);
      }}
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
  | { name: 'unknownTag' };

function FuelFlow({
  device,
  session,
  onSignOut,
  onSessionEnded,
  onDeviceEnded,
}: {
  device: DeviceIdentity;
  session: AgentSession;
  onSignOut: () => void;
  onSessionEnded: () => void;
  onDeviceEnded: () => void;
}) {
  const { t } = useTranslation('pos');
  const errorText = useErrorText();
  const [step, setStep] = useState<FuelStep>({ name: 'home' });

  /** Sends the fueling. A retry after a lost answer reuses the same clientTxnId, so it is charged once. */
  const send = async (tagUid: string, litres: string, clientTxnId: string) => {
    const volumeMl = parseLitres(litres);
    if (volumeMl === null) return;
    setStep({ name: 'sending', tagUid, litres, clientTxnId });
    try {
      const fueling = await recordFueling(device, session, {
        clientTxnId,
        tagUid,
        volumeMl,
        deviceAt: new Date().toISOString(),
        receiptLocale: (i18n.language === 'en' ? 'en' : 'ar') as Locale,
      });
      setStep({ name: 'result', fueling });
    } catch (cause) {
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
