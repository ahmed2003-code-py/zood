/**
 * The POS against the live API (VITE_API_MODE=http). The terminal is this browser: it activates
 * once with the code Ops issued (its own key pair stays here), the agent signs in with Worker ID and
 * PIN, and each fueling is decided by the server (B4 contract). RFID is simulated: the agent types
 * or pastes the tag UID that a reader would return (simulator tools can remember test UIDs).
 *
 * No API contract changes here: every call is the one the native POS makes too.
 */
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Lock, LogOut } from 'lucide-react';
import { ApiRequestError } from '@/api/http-client';
import { Banner, BrandLogo, Button, Icon, Numpad, TextField } from '@/design-system';
import { cn } from '@/lib/cn';
import { normalizeDigits } from '@/live/input';
import { formatCountdown } from './flow';
import { FuelFlow } from './FuelFlow';
import {
  activate,
  agentSignIn,
  agentSignOut,
  changePin,
  deactivateHere,
  DEVICE_ENDED,
  loadDevice,
  loadSession,
  saveSession,
  SESSION_ENDED,
  type AgentSession,
  type DeviceIdentity,
} from './pos-api';
import { clearShift } from './shift-log';
import { readSignInRefusal, type SignInRefusal } from './sign-in-state';
import { DisplayPreferences, ErrorLine, PosHeader, Screen, Step, SyncPanel } from './ui';
import { useErrorText } from './use-error-text';
import { useOffline, type OfflineState } from './use-offline';

const PIN_LENGTH = 6;

export function LivePos() {
  const [device, setDevice] = useState<DeviceIdentity | undefined>(loadDevice);
  const [session, setSession] = useState<AgentSession | undefined>(loadSession);
  const [notice, setNotice] = useState<string>();

  const deviceEnded = () => {
    void deactivateHere();
    clearShift();
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
        setNotice('signedOut');
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
  const { t, i18n } = useTranslation('pos');
  const offline = useOffline(device, session && !session.agent.mustChangePin ? session : undefined);
  const station = offline.snapshot?.station;
  const title = station ? (i18n.language === 'en' ? station.nameEn : station.nameAr) : t('live.login.device', { code: device.deviceCode });

  if (session && !session.agent.mustChangePin) {
    return <FuelFlow device={device} session={session} offline={offline} onSessionEnded={onSessionEnded} onDeviceEnded={onDeviceEnded} onSignOut={onSignOut} />;
  }
  return (
    <>
      <PosHeader
        title={title}
        agent={session ? { name: session.agent.fullName, workerId: session.agent.workerId } : undefined}
        state={offline}
        menu={() => (
          <>
            <DisplayPreferences />
            <SyncPanel state={offline} />
          </>
        )}
      />
      {session ? (
        <Step id="pin">
          <PinChangeScreen device={device} session={session} onChanged={onPinChanged} onSessionEnded={onSessionEnded} onSignOut={onSignOut} />
        </Step>
      ) : (
        <Step id="login">
          <AgentLoginScreen device={device} notice={notice} offline={offline} onDeviceEnded={onDeviceEnded} onSignedIn={onSignedIn} />
        </Step>
      )}
    </>
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
    <>
      <PosHeader title={t('frameLabel')} menu={() => <DisplayPreferences />} />
      <Step id="activate">
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
          <p className="text-xs text-muted">{t('live.activate.expiredHint')}</p>
        </Screen>
      </Step>
    </>
  );
}

// ---------------------------------------------------------------------------------------- PIN

function PinPad({ label, onComplete, error, disabled }: { label: string; onComplete: (pin: string) => void; error?: string; disabled?: boolean }) {
  const [pin, setPin] = useState('');
  const add = (digit: string) => {
    if (disabled || pin.length >= PIN_LENGTH) return;
    const next = pin + digit;
    setPin(next);
    if (next.length === PIN_LENGTH) {
      setPin('');
      onComplete(next);
    }
  };
  return (
    <div className={cn('flex w-full flex-col items-center gap-3', disabled && 'opacity-60')}>
      <span className="text-base font-medium">{label}</span>
      <div dir="ltr" className="flex gap-3" aria-hidden>
        {Array.from({ length: PIN_LENGTH }, (_, index) => (
          <span key={index} className={cn('size-4 rounded-full border-2 transition-colors duration-150', index < pin.length ? 'border-primary bg-primary' : 'border-border-strong')} />
        ))}
      </div>
      <ErrorLine text={error} />
      <Numpad className="w-full" onDigit={add} onBackspace={() => setPin((value) => value.slice(0, -1))} />
    </div>
  );
}

/** Ticks every second while a lockout runs; undefined once it is over. */
function useLockCountdown(lockedUntilMs: number | undefined) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!lockedUntilMs) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [lockedUntilMs]);
  return lockedUntilMs && lockedUntilMs > now ? formatCountdown(lockedUntilMs, now) : undefined;
}

/** The refusal in words: attempts left when the API says, a countdown for a lockout. */
function RefusalText({ refusal, countdown }: { refusal?: SignInRefusal; countdown?: string }) {
  const { t } = useTranslation('pos');
  if (!refusal) return null;
  if (countdown) {
    return (
      <div role="alert" className="flex w-full items-center gap-3 rounded-control border border-danger/30 bg-danger-subtle p-3 text-start text-danger">
        <Icon icon={Lock} />
        <div className="min-w-0 flex-1">
          <p className="font-semibold">{t('live.lock.title')}</p>
          <p className="text-sm">{t('live.lock.hint')}</p>
        </div>
        <span className="text-2xl font-semibold tabular-nums" dir="ltr">
          {countdown}
        </span>
      </div>
    );
  }
  return null;
}

function AgentLoginScreen({
  device,
  notice,
  offline,
  onSignedIn,
  onDeviceEnded,
}: {
  device: DeviceIdentity;
  notice?: string;
  offline: OfflineState;
  onSignedIn: (session: AgentSession) => void;
  onDeviceEnded: () => void;
}) {
  const { t } = useTranslation('pos');
  const errorText = useErrorText();
  const [workerId, setWorkerId] = useState('');
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<SignInRefusal & { workerId: string }>();
  const shown = refusal && refusal.workerId === workerId.trim().toUpperCase() ? refusal : undefined;
  const countdown = useLockCountdown(shown?.lockedUntilMs);

  const submit = async (pin: string) => {
    setBusy(true);
    setRefusal(undefined);
    const worker = workerId.trim().toUpperCase();
    try {
      onSignedIn(await agentSignIn(device, worker, pin));
    } catch (cause) {
      if (cause instanceof ApiRequestError && DEVICE_ENDED.has(cause.code)) {
        onDeviceEnded();
        return;
      }
      setRefusal({ ...readSignInRefusal(cause, Date.now()), workerId: worker });
    } finally {
      setBusy(false);
    }
  };

  const message = !shown || countdown
    ? undefined
    : shown.code === 'AGENT_LOCKED'
      ? t('live.lock.over')
      : shown.attemptsLeft !== undefined
        ? t('live.lock.attemptsLeft', { count: shown.attemptsLeft })
        : errorText(shown.code);

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-5 py-5">
      <div className="flex flex-col items-center gap-1.5 text-center">
        <h1 className="text-xl font-semibold">{t('login.title')}</h1>
        <p className="text-sm text-muted">
          {notice === 'sessionEnded' ? t('live.login.sessionEnded') : notice === 'signedOut' ? t('live.login.signedOut') : t('live.login.hint')}
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
      <RefusalText refusal={shown} countdown={countdown} />
      <PinPad label={t('login.pin')} disabled={busy || !workerId.trim() || Boolean(countdown)} error={message} onComplete={(pin) => void submit(pin)} />
      {(offline.queue.length > 0 || offline.rejectedCount > 0) && <Banner tone="warning" title={t('live.offline.pending', { count: offline.queue.length })} description={t('live.login.queueHint')} />}
    </div>
  );
}

/**
 * The first sign-in with the PIN Operations gave: the agent picks their own. Signing out is always
 * there, so an agent who does not know the PIN is never stuck on this screen with a shift open.
 */
function PinChangeScreen({
  device,
  session,
  onChanged,
  onSessionEnded,
  onSignOut,
}: {
  device: DeviceIdentity;
  session: AgentSession;
  onChanged: (session: AgentSession) => void;
  onSessionEnded: () => void;
  onSignOut: () => void;
}) {
  const { t } = useTranslation('pos');
  const errorText = useErrorText();
  const [step, setStep] = useState<{ stage: 'current' } | { stage: 'new'; current: string } | { stage: 'repeat'; current: string; next: string }>({
    stage: 'current',
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [lockedUntil, setLockedUntil] = useState<number>();
  const countdown = useLockCountdown(lockedUntil);

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
      const refusal = readSignInRefusal(cause, Date.now());
      if (refusal.lockedUntilMs) setLockedUntil(refusal.lockedUntilMs);
      setError(refusal.lockedUntilMs ? undefined : refusal.attemptsLeft !== undefined ? t('live.lock.attemptsLeft', { count: refusal.attemptsLeft }) : errorText(cause));
      if (cause instanceof ApiRequestError && (cause.field === 'currentPin' || refusal.lockedUntilMs)) setStep({ stage: 'current' });
      else setStep({ stage: 'new', current: step.current });
    } finally {
      setBusy(false);
    }
  };

  const stages = ['current', 'new', 'repeat'] as const;
  return (
    <Screen
      className="gap-4"
      actions={
        <Button variant="secondary" size="lg" fullWidth leadingIcon={LogOut} onClick={onSignOut}>
          {t('live.pin.signOut')}
        </Button>
      }
    >
      <div className="flex flex-col items-center gap-1.5">
        <h1 className="text-xl font-semibold">{t('live.pin.title')}</h1>
        <p className="text-sm text-muted">{t('live.pin.hint')}</p>
      </div>
      <ol className="flex items-center gap-2" aria-label={t('live.pin.progress', { step: stages.indexOf(step.stage) + 1, total: stages.length })}>
        {stages.map((stage, index) => (
          <li
            key={stage}
            className={cn('h-1.5 w-10 rounded-full transition-colors duration-150', index <= stages.indexOf(step.stage) ? 'bg-primary' : 'bg-border-strong')}
          />
        ))}
      </ol>
      <RefusalText refusal={countdown ? { code: 'AGENT_LOCKED' } : undefined} countdown={countdown} />
      <PinPad key={step.stage} label={t(`live.pin.${step.stage}`)} disabled={busy || Boolean(countdown)} error={error} onComplete={(pin) => void complete(pin)} />
      <p className="text-xs text-muted">{t('live.pin.forgot')}</p>
    </Screen>
  );
}
