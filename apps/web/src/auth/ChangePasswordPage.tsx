import { KeyRound } from 'lucide-react';
import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate, useNavigate, useSearchParams } from 'react-router';
import { ApiRequestError } from '@/api/http-client';
import { AppSplash } from '@/app/RootLayout';
import { Button, TextField, toast } from '@/design-system';
import { authApi } from './auth-api';
import { AuthLayout, FormError } from './AuthLayout';
import { authErrorMessage } from './errors';
import { HOME_BY_ROLE, loginPath, safeNext } from './portals';
import { useSession } from './session-store';

/** Matches the API's rule (contract §6); the server is the authority, this only saves a round trip. */
export const MIN_PASSWORD_LENGTH = 12;

/** Change password: forced after an Ops-issued temporary password, or chosen from the account menu. */
export function ChangePasswordPage() {
  const { t } = useTranslation('auth');
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const { status, user, door, restore, setUser } = useSession();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [submitted, setSubmitted] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<{ message: string; reference?: string }>();

  useEffect(() => {
    void restore();
  }, [restore]);

  if (status === 'unknown') return <AppSplash />;
  if (status === 'anonymous' || !user) return <Navigate to={loginPath(door)} replace />;

  const tooShort = next.length < MIN_PASSWORD_LENGTH;
  const mismatch = confirm !== next;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setSubmitted(true);
    if (busy || tooShort || mismatch || !current) return;
    setBusy(true);
    setFailure(undefined);
    try {
      await authApi.changePassword(current, next);
      setUser(await authApi.me());
      toast(t('password.saved'));
      navigate(safeNext(params.get('next')) ?? HOME_BY_ROLE[user.role], { replace: true });
    } catch (error) {
      setFailure(
        error instanceof ApiRequestError && error.code === 'INVALID_CREDENTIALS'
          ? { message: t('password.wrongCurrent') }
          : authErrorMessage(t, error),
      );
    } finally {
      setBusy(false);
    }
  };

  return (
    <AuthLayout
      title={t('password.title')}
      description={user.mustChangePassword ? t('password.forcedDescription') : t('password.description')}
    >
      <form className="flex flex-col gap-4" onSubmit={submit} noValidate>
        <FormError {...failure} />
        {/* Lets password managers attach the new password to the right account. */}
        <input type="email" name="username" autoComplete="username" value={user.email} readOnly hidden />
        <TextField
          label={t('password.current')}
          type="password"
          autoComplete="current-password"
          dir="ltr"
          leadingIcon={KeyRound}
          value={current}
          onChange={(event) => setCurrent(event.target.value)}
        />
        <TextField
          label={t('password.new')}
          type="password"
          autoComplete="new-password"
          dir="ltr"
          hint={t('password.rule', { min: MIN_PASSWORD_LENGTH })}
          error={submitted && tooShort ? t('password.tooShort', { min: MIN_PASSWORD_LENGTH }) : undefined}
          value={next}
          onChange={(event) => setNext(event.target.value)}
        />
        <TextField
          label={t('password.confirm')}
          type="password"
          autoComplete="new-password"
          dir="ltr"
          error={submitted && mismatch ? t('password.mismatch') : undefined}
          value={confirm}
          onChange={(event) => setConfirm(event.target.value)}
        />
        <Button type="submit" size="lg" fullWidth loading={busy}>
          {t('password.submit')}
        </Button>
      </form>
    </AuthLayout>
  );
}
