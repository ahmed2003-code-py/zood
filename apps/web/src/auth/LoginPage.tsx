import { Copy, Eye, EyeOff, KeyRound, Mail, ShieldCheck } from 'lucide-react';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate, useNavigate, useSearchParams } from 'react-router';
import { ApiRequestError } from '@/api/http-client';
import { Button, IconButton, TextField, toast } from '@/design-system';
import { QrCode } from '@/features/receipt/QrCode';
import { authApi } from './auth-api';
import { AuthLayout, FormError } from './AuthLayout';
import { authErrorMessage } from './errors';
import { HOME_BY_ROLE, safeNext } from './portals';
import { useSession } from './session-store';
import type { Door, LoginResult, MfaEnrollment } from './types';

type Step =
  | { kind: 'credentials' }
  | { kind: 'mfa'; challengeToken: string }
  | { kind: 'enroll'; challengeToken: string; enrollment?: MfaEnrollment };

type Failure = { message: string; reference?: string };

const CODE_PATTERN = /^\d{6}$/;

/** Sign-in for every web role. `door="admin"` is the Super Admin's separate sign-in (PRD §6.4). */
export function LoginPage({ door }: { door: Door }) {
  const { t } = useTranslation('auth');
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const session = useSession();
  const restore = useSession((state) => state.restore);
  const [step, setStep] = useState<Step>({ kind: 'credentials' });
  const [failure, setFailure] = useState<Failure>();
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void restore();
  }, [restore]);

  const finish = (result: LoginResult) => {
    if (result.status === 'MFA_REQUIRED') return setStep({ kind: 'mfa', challengeToken: result.challengeToken });
    if (result.status === 'MFA_ENROLLMENT_REQUIRED') return setStep({ kind: 'enroll', challengeToken: result.challengeToken });
    session.establish(door, result.session);
    const next = safeNext(params.get('next'));
    const { user } = result.session;
    if (user.mustChangePassword) {
      navigate(`/account/password${next ? `?next=${encodeURIComponent(next)}` : ''}`, { replace: true });
    } else {
      navigate(next ?? HOME_BY_ROLE[user.role], { replace: true });
    }
  };

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setFailure(undefined);
    try {
      await action();
    } catch (error) {
      const message = authErrorMessage(t, error);
      setFailure(message);
      // An expired challenge cannot be retried: start again from the password.
      if (error instanceof ApiRequestError && error.code === 'MFA_CHALLENGE_EXPIRED') setStep({ kind: 'credentials' });
    } finally {
      setBusy(false);
    }
  };

  // Already signed in (e.g. the sign-in page opened in a second tab): go straight in.
  if (session.status === 'authenticated' && session.user && step.kind === 'credentials') {
    return <Navigate to={safeNext(params.get('next')) ?? HOME_BY_ROLE[session.user.role]} replace />;
  }

  const restart = () => {
    setFailure(undefined);
    setStep({ kind: 'credentials' });
  };

  if (step.kind === 'mfa') {
    return (
      <AuthLayout title={t('mfa.title')} description={t('mfa.description')}>
        <CodeForm
          busy={busy}
          failure={failure}
          submitLabel={t('mfa.submit')}
          onSubmit={(code) => run(async () => finish(await authApi.verifyMfa(step.challengeToken, code)))}
        />
        <Button variant="ghost" fullWidth className="mt-2" onClick={restart}>
          {t('mfa.back')}
        </Button>
      </AuthLayout>
    );
  }

  if (step.kind === 'enroll') {
    return (
      <AuthLayout title={t('enroll.title')} description={t('enroll.description')}>
        <Enrollment
          step={step}
          busy={busy}
          failure={failure}
          onLoaded={(enrollment) => setStep({ ...step, enrollment })}
          onFailure={setFailure}
          onSubmit={(code) => run(async () => finish(await authApi.confirmEnrollment(step.challengeToken, code)))}
        />
        <Button variant="ghost" fullWidth className="mt-2" onClick={restart}>
          {t('mfa.back')}
        </Button>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout
      title={door === 'admin' ? t('signIn.adminTitle') : t('signIn.title')}
      description={door === 'admin' ? t('signIn.adminSubtitle') : t('signIn.subtitle')}
    >
      <CredentialsForm
        busy={busy}
        failure={failure}
        onSubmit={(email, password) => run(async () => finish(await authApi.login(door, email, password)))}
      />
    </AuthLayout>
  );
}

function CredentialsForm({
  busy,
  failure,
  onSubmit,
}: {
  busy: boolean;
  failure?: Failure;
  onSubmit: (email: string, password: string) => void;
}) {
  const { t } = useTranslation('auth');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [visible, setVisible] = useState(false);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!busy) onSubmit(email.trim(), password);
  };

  return (
    <form className="flex flex-col gap-4" onSubmit={submit} noValidate>
      <FormError {...failure} />
      <TextField
        label={t('signIn.email')}
        type="email"
        name="email"
        autoComplete="username"
        inputMode="email"
        dir="ltr"
        required
        leadingIcon={Mail}
        value={email}
        onChange={(event) => setEmail(event.target.value)}
      />
      <TextField
        label={t('signIn.password')}
        type={visible ? 'text' : 'password'}
        name="password"
        autoComplete="current-password"
        dir="ltr"
        required
        leadingIcon={KeyRound}
        value={password}
        onChange={(event) => setPassword(event.target.value)}
        trailing={
          <IconButton
            icon={visible ? EyeOff : Eye}
            label={visible ? t('signIn.hidePassword') : t('signIn.showPassword')}
            onClick={() => setVisible((value) => !value)}
          />
        }
      />
      <Button type="submit" size="lg" fullWidth loading={busy} disabled={!email.trim() || !password}>
        {t('signIn.submit')}
      </Button>
      <p className="text-center text-xs text-muted">{t('signIn.forgot')}</p>
    </form>
  );
}

function CodeForm({
  busy,
  failure,
  submitLabel,
  onSubmit,
}: {
  busy: boolean;
  failure?: Failure;
  submitLabel: string;
  onSubmit: (code: string) => void;
}) {
  const { t } = useTranslation('auth');
  const [code, setCode] = useState('');

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!busy && CODE_PATTERN.test(code)) onSubmit(code);
  };

  return (
    <form className="flex flex-col gap-4" onSubmit={submit} noValidate>
      <FormError {...failure} />
      <TextField
        label={t('mfa.code')}
        name="one-time-code"
        autoComplete="one-time-code"
        inputMode="numeric"
        dir="ltr"
        maxLength={6}
        autoFocus
        leadingIcon={ShieldCheck}
        className="[&_input]:text-center [&_input]:text-xl [&_input]:tracking-[0.4em] [&_input]:tabular-nums"
        value={code}
        onChange={(event) => setCode(event.target.value.replace(/\D/g, '').slice(0, 6))}
      />
      <Button type="submit" size="lg" fullWidth loading={busy} disabled={!CODE_PATTERN.test(code)}>
        {submitLabel}
      </Button>
    </form>
  );
}

function Enrollment({
  step,
  busy,
  failure,
  onLoaded,
  onFailure,
  onSubmit,
}: {
  step: Extract<Step, { kind: 'enroll' }>;
  busy: boolean;
  failure?: Failure;
  onLoaded: (enrollment: MfaEnrollment) => void;
  onFailure: (failure: Failure) => void;
  onSubmit: (code: string) => void;
}) {
  const { t } = useTranslation('auth');
  const { enrollment, challengeToken } = step;
  // The callbacks change on every parent render; the request must run once per challenge.
  const callbacks = useRef({ onLoaded, onFailure, t });
  useEffect(() => {
    callbacks.current = { onLoaded, onFailure, t };
  });

  useEffect(() => {
    if (enrollment) return;
    let cancelled = false;
    authApi.startEnrollment(challengeToken).then(
      (loaded) => {
        if (!cancelled) callbacks.current.onLoaded(loaded);
      },
      (error: unknown) => {
        if (!cancelled) callbacks.current.onFailure(authErrorMessage(callbacks.current.t, error));
      },
    );
    return () => {
      cancelled = true;
    };
  }, [challengeToken, enrollment]);

  const copy = async () => {
    if (!enrollment) return;
    try {
      await navigator.clipboard.writeText(enrollment.secret);
      toast(t('enroll.copied'));
    } catch {
      // Clipboard unavailable: the key is on screen to type by hand.
    }
  };

  return (
    <div className="flex flex-col gap-5">
      <ol className="flex list-decimal flex-col gap-4 ps-5 text-sm">
        <li>{t('enroll.step1')}</li>
        <li>
          <p>{t('enroll.step2')}</p>
          {enrollment && (
            <div className="mt-3 flex flex-col items-center gap-3">
              <div className="rounded-container bg-paper p-3 text-paper-ink">
                <QrCode value={enrollment.otpauthUri} size={176} label={t('enroll.qrLabel')} />
              </div>
              <div className="w-full">
                <p className="text-xs text-muted">{t('enroll.manual')}</p>
                <div className="mt-1 flex items-center gap-2">
                  <code dir="ltr" className="flex-1 rounded-control bg-surface-sunken px-3 py-2 font-mono text-sm break-all">
                    {enrollment.secret.replace(/(.{4})/g, '$1 ').trim()}
                  </code>
                  <IconButton icon={Copy} label={t('enroll.copy')} onClick={() => void copy()} />
                </div>
              </div>
            </div>
          )}
        </li>
        <li>{t('enroll.step3')}</li>
      </ol>
      <CodeForm busy={busy || !enrollment} failure={failure} submitLabel={t('enroll.submit')} onSubmit={onSubmit} />
    </div>
  );
}
