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
  'SELF_APPROVAL_NOT_ALLOWED',
  'BANK_ACCOUNT_REQUIRED',
  'PAYOUT_EXCEEDS_AVAILABLE',
  'PAYOUT_ON_HOLD',
  'VALUE_DATE_TOO_FAR',
  'PAYOUT_NOT_PENDING',
  'PAYOUT_BANK_ACCOUNT_CHANGED',
  'BANK_ACCOUNT_CHANGE_PENDING',
  'BANK_ACCOUNT_CHANGE_NOT_PENDING',
  'INVALID_BANK_ACCOUNT',
  'FUELING_NOT_HELD',
  'HOLD_ALREADY_RELEASED',
  'FUELING_NOT_REVERSIBLE',
  'FUELING_REVERSED',
  'REVERSAL_EXISTS',
  'REVERSAL_NOT_PENDING',
  'TICKET_CLOSED',
  'TICKET_TYPE_NOT_ALLOWED',
  'TICKET_LINK_NOT_FOUND',
  'TOPUP_NOT_AWAITING_APPROVAL',
  'UNKNOWN_RECEIVING_ACCOUNT',
  'ADJUSTMENT_NOT_PENDING',
  'IDEMPOTENCY_KEY_REUSED',
  'INVALID_IDEMPOTENCY_KEY',
  'FLEET_CODE_IN_USE',
  'INVALID_FLEET_CODE',
  'INVALID_TANK_CAPACITY',
  'INVALID_YEAR',
  'IMPORT_FILE_REQUIRED',
  'IMPORT_FILE_TYPE',
  'IMPORT_FILE_EMPTY',
  'IMPORT_FILE_ENCODING',
  'IMPORT_FILE_MALFORMED',
  'IMPORT_FILE_UNSAFE',
  'IMPORT_HEADER_INVALID',
  'IMPORT_TOO_MANY_ROWS',
  'IMPORT_FILE_TOO_LARGE',
  'IMPORT_HAS_PROBLEMS',
  'IMPORT_NOTHING_TO_IMPORT',
  'IMPORT_ALREADY_RUNNING',
  'IMPORT_NOT_CONFIRMABLE',
  'IMPORT_NOT_CANCELLABLE',
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
