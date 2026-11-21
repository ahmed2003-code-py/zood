/**
 * What a refused agent sign-in or PIN change tells the terminal (pure, tested): a lockout with the
 * time it ends (`AGENT_LOCKED`, `params.retryAfterSeconds`, B4 contract §1) and, when the API says
 * so, how many attempts are left before the next lockout.
 */
import { ApiRequestError } from '@/api/http-client';

export interface SignInRefusal {
  code: string;
  /** Epoch ms when a lockout ends; set only for `AGENT_LOCKED`. */
  lockedUntilMs?: number;
  /** Attempts left before a lockout, when the API sends it. */
  attemptsLeft?: number;
}

function wholeNumber(value: unknown): number | undefined {
  const number = typeof value === 'number' ? value : typeof value === 'string' && value.trim() !== '' ? Number(value) : NaN;
  return Number.isInteger(number) && number >= 0 ? number : undefined;
}

/** Reads a refusal. A lockout without a duration is taken as the API's standard 15 minutes. */
export function readSignInRefusal(error: unknown, nowMs: number): SignInRefusal {
  if (!(error instanceof ApiRequestError)) return { code: 'UNKNOWN' };
  const params = (error.params ?? {}) as Record<string, unknown>;
  const attemptsLeft = wholeNumber(params.attemptsLeft ?? params.attemptsRemaining ?? params.remainingAttempts);
  if (error.code === 'AGENT_LOCKED') {
    const seconds = wholeNumber(params.retryAfterSeconds) ?? error.retryAfterSeconds ?? 15 * 60;
    return { code: error.code, lockedUntilMs: nowMs + Math.max(1, seconds) * 1000 };
  }
  return { code: error.code, attemptsLeft };
}
