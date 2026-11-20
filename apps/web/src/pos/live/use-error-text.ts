import { useTranslation } from 'react-i18next';
import { ApiRequestError } from '@/api/http-client';

/** The sentence for a refusal, from its code; never the server's English message. */
export function useErrorText() {
  const { t } = useTranslation('pos');
  return (error: unknown): string => {
    const code = error instanceof ApiRequestError ? error.code : typeof error === 'string' ? error : 'UNKNOWN';
    return t(`live.errors.${code}`, { defaultValue: t('live.errors.UNKNOWN') });
  };
}
