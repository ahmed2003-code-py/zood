import type { TFunction } from 'i18next';
import { ApiRequestError, NETWORK_ERROR } from '@/api/http-client';

/** Codes the master-data screens have their own sentence for (contract B2 §4, plus the common ones). */
const KNOWN = [
  NETWORK_ERROR,
  'VALIDATION_FAILED',
  'FORBIDDEN',
  'NOT_FOUND',
  'RATE_LIMITED',
  'TAX_ID_IN_USE',
  'DEPARTMENT_EXISTS',
  'COMPANY_NOT_FOUND',
  'OPERATOR_NOT_FOUND',
  'STATION_NOT_FOUND',
  'DEPARTMENT_NOT_FOUND',
  'PLATE_IN_USE',
  'INVALID_PLATE',
  'VIP_CEILING_ONLY_FOR_VIP',
  'VEHICLE_DECOMMISSIONED',
  'VEHICLE_NOT_ACTIVE',
  'INVALID_TAG_UID',
  'TAG_ALREADY_BOUND',
  'VEHICLE_HAS_ACTIVE_TAG',
  'INVALID_TAG_TRANSITION',
  'INVALID_TIME_ZONE',
  'INCOMPLETE_LOCATION',
  'STATION_CLOSED',
  'DEVICE_NOT_PENDING',
  'AGENT_DEACTIVATED',
  'PRICE_BACKDATED',
  'PRICE_TOO_FAR_AHEAD',
  'PRICE_EXISTS',
  'CONCURRENT_CHANGE',
  'SCOPE_NOT_FOUND',
  'EMAIL_IN_USE',
  'SCOPE_MISMATCH',
  'SELF_CHANGE_NOT_ALLOWED',
  'LAST_SUPER_ADMIN',
  'BANK_REFERENCE_IN_USE',
  'INVALID_BANK_REFERENCE',
  'TOPUP_NOT_PENDING',
  'TRANSFER_DATE_IN_FUTURE',
  'RECONCILIATION_RUNNING',
] as const;

type KnownCode = (typeof KNOWN)[number];

const isKnown = (code: string): code is KnownCode => (KNOWN as readonly string[]).includes(code);

export interface LiveError {
  message: string;
  /** "Reference: …" for unexpected failures, so support can find the server log line. */
  reference?: string;
  /** The request field the API blamed, so the form can mark it. */
  field?: string;
}

/** The sentence for a failed request, chosen by the error's code, never by the server's English message. */
export function liveError(t: TFunction<'live'>, error: unknown): LiveError {
  if (!(error instanceof ApiRequestError)) return { message: t('errors.generic') };
  const field = error.field ?? error.fields?.[0]?.field;
  if (isKnown(error.code)) return { message: t(`errors.${error.code}`), field };
  return {
    message: t('errors.generic'),
    reference: error.requestId ? t('errors.reference', { requestId: error.requestId }) : undefined,
    field,
  };
}
