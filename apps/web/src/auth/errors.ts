import type { TFunction } from 'i18next';
import { ApiRequestError, NETWORK_ERROR } from '@/api/http-client';

const KNOWN = [
  'INVALID_CREDENTIALS',
  'ACCOUNT_LOCKED',
  'ACCOUNT_DISABLED',
  'MFA_INVALID_CODE',
  'MFA_CHALLENGE_EXPIRED',
  'PASSWORD_TOO_WEAK',
  'PASSWORD_REUSED',
  'RATE_LIMITED',
  NETWORK_ERROR,
] as const;

type KnownCode = (typeof KNOWN)[number];

const isKnown = (code: string): code is KnownCode => (KNOWN as readonly string[]).includes(code);

/** The sentence for a sign-in failure, chosen by the error's code, never by the server's English message. */
export function authErrorMessage(t: TFunction<'auth'>, error: unknown): { message: string; reference?: string } {
  if (!(error instanceof ApiRequestError)) return { message: t('errors.generic') };
  const reference = error.requestId ? t('errors.reference', { requestId: error.requestId }) : undefined;
  if (!isKnown(error.code)) return { message: t('errors.generic'), reference };
  const retryAfter = Number(error.params?.retryAfterSeconds ?? 0);
  const minutes = Math.max(1, Math.ceil(retryAfter / 60));
  const min = Number(error.params?.minLength ?? 12);
  // Expected refusals carry no reference: it would only add noise next to "wrong password".
  return { message: t(`errors.${error.code}`, { minutes, min }) };
}
