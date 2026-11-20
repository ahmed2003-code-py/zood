/**
 * The demo form's logic, kept apart from the component so it can be tested without a browser:
 * the request body (with the honeypot and the render time) and how a failed send is explained.
 */
import { ApiRequestError, NETWORK_ERROR } from '@/api/http-client';
import type { DemoRequestBody } from '@/api/types';
import { DEMO_FIELDS, type DemoField, type DemoRequestValues } from '@/domain/demo-request';
import type { Locale } from '@/lib/locale';

export interface BotSignals {
  /** The hidden honeypot field, sent exactly as found so the server can judge it. */
  website: string;
  /** When the form rendered, in ms since the epoch. */
  startedAt: number;
}

export function buildDemoRequest(values: DemoRequestValues, locale: Locale, signals: BotSignals): DemoRequestBody {
  return { ...values, locale, website: signals.website, startedAt: signals.startedAt };
}

export type SendFailure =
  | { kind: 'rateLimited'; retryMinutes?: number }
  | { kind: 'validation'; fields: DemoField[] }
  | { kind: 'network' }
  | { kind: 'generic'; requestId?: string };

const isDemoField = (field: string | undefined): field is DemoField => (DEMO_FIELDS as readonly (string | undefined)[]).includes(field);

/** Turns whatever the send threw into the state the form shows. */
export function describeSendFailure(error: unknown): SendFailure {
  if (!(error instanceof ApiRequestError)) return { kind: 'generic' };
  if (error.code === 'RATE_LIMITED' || error.status === 429) {
    return { kind: 'rateLimited', retryMinutes: error.retryAfterSeconds ? Math.ceil(error.retryAfterSeconds / 60) : undefined };
  }
  if (error.code === 'VALIDATION_FAILED') {
    const named = [error.field, ...(error.fields ?? []).map((issue) => issue.field)].filter(isDemoField);
    return { kind: 'validation', fields: [...new Set(named)] };
  }
  if (error.code === NETWORK_ERROR || error.status === 0) return { kind: 'network' };
  return { kind: 'generic', requestId: error.requestId };
}
