/** The Ops "Demo requests" screen's rules, apart from the component so they can be tested. */
import type { Lead, LeadQuery, LeadUpdate } from '@/api/types';
import type { Tone } from '@/design-system';
import { LEAD_STATUSES, type LeadStatus } from '@/domain/demo-request';

export type LeadFilter = 'ALL' | LeadStatus;
export const LEAD_FILTERS: readonly LeadFilter[] = ['ALL', ...LEAD_STATUSES];
export const LEAD_NOTE_MAX = 1000;

export const LEAD_TONE: Record<LeadStatus, Tone> = {
  NEW: 'info',
  CONTACTED: 'brand',
  QUALIFIED: 'success',
  CLOSED: 'neutral',
};

export function leadQuery(filter: LeadFilter, page: { cursor?: string; limit?: number }): LeadQuery {
  return { ...page, status: filter === 'ALL' ? undefined : filter };
}

/**
 * The PATCH body: always the status; the note only when it changed (an emptied note is sent as an
 * empty string, which clears it). Undefined when nothing changed, so no request is sent.
 */
export function leadUpdate(lead: Lead, status: LeadStatus, noteInput: string): LeadUpdate | undefined {
  const note = noteInput.trim();
  const noteChanged = note !== (lead.note ?? '');
  if (!noteChanged && status === lead.status) return undefined;
  return noteChanged ? { status, note } : { status };
}

/** wa.me wants the international number without + or leading zero: 01012345678 → 201012345678. */
export function whatsappLink(phone: string): string {
  return `https://wa.me/2${phone}`;
}
