/**
 * The in-browser stand-in for the demo-request endpoints, so the landing form and the Ops "Leads"
 * screen work in the investor demo without a server. Loaded on first use (see mock-api.ts).
 *
 * It applies the same rules as the API: the shared validation, a silent drop when the honeypot is
 * filled, and a per-browser rate limit that answers 429 RATE_LIMITED with a retry time.
 * It does not check `startedAt` timing: that heuristic belongs to the server.
 */
import { ApiRequestError } from '@/api/http-client';
import type { DemoRequestBody, DemoRequestReceipt, Lead, LeadPage, LeadQuery, LeadUpdate } from '@/api/types';
import { LEAD_STATUSES, validateDemoRequest, type DemoFieldError } from '@/domain/demo-request';
import { isLocale } from '@/lib/locale';
import { readStorage, writeStorage } from '@/lib/storage';

const STORAGE_KEY = 'zood.demo.leads.v1';
/** Five requests per ten minutes from one browser; the server's own limit is per address. */
export const MOCK_RATE_LIMIT = { max: 5, windowMs: 10 * 60_000 } as const;
const NOTE_MAX = 500;

interface LeadsState {
  sequence: number;
  leads: Lead[];
  /** When this browser sent its recent requests (ms), for the rate limit. */
  sent: number[];
}

const HOUR = 60 * 60_000;

/** Three example leads so the Ops screen has something to show in the demo. */
function seed(now: number): LeadsState {
  const at = (hoursAgo: number) => new Date(now - hoursAgo * HOUR).toISOString();
  const lead = (n: number, fields: Omit<Lead, 'id' | 'reference' | 'updatedAt'>): Lead => ({
    id: `lead-demo-${n}`,
    reference: reference(n),
    updatedAt: fields.createdAt,
    ...fields,
  });
  return {
    sequence: 3,
    sent: [],
    leads: [
      lead(3, {
        companyName: 'النور للنقل الثقيل',
        contactName: 'محمود عبد الحميد',
        phone: '01001234567',
        email: 'm.abdelhamid@example.com',
        fleetSize: '50-99',
        locale: 'ar',
        status: 'NEW',
        note: null,
        createdAt: at(2),
      }),
      lead(2, {
        companyName: 'Green Valley Foods',
        contactName: 'Sara Fawzy',
        phone: '01112345678',
        email: 'sara.fawzy@example.com',
        fleetSize: '20-49',
        locale: 'en',
        status: 'CONTACTED',
        note: 'Call back on Sunday with pricing for 30 vans.',
        createdAt: at(26),
      }),
      lead(1, {
        companyName: 'الصعيد للمقاولات',
        contactName: 'أحمد رمضان',
        phone: '01223456789',
        email: 'ahmed.ramadan@example.com',
        fleetSize: '100-249',
        locale: 'ar',
        status: 'QUALIFIED',
        note: 'عايزين تشغيل تجريبي على 20 عربية في أسيوط.',
        createdAt: at(74),
      }),
    ],
  };
}

function reference(sequence: number): string {
  return `LD-${String(sequence).padStart(5, '0')}`;
}

let memory: LeadsState | undefined;

function load(now: number): LeadsState {
  if (memory) return memory;
  try {
    const stored = readStorage(STORAGE_KEY);
    if (stored) memory = JSON.parse(stored) as LeadsState;
  } catch {
    memory = undefined;
  }
  memory ??= seed(now);
  return memory;
}

function save(state: LeadsState): void {
  memory = state;
  writeStorage(STORAGE_KEY, JSON.stringify(state));
}

/** Forgets every change (tests, and a fresh demo). */
export function resetMockLeads(): void {
  memory = undefined;
  writeStorage(STORAGE_KEY, '');
}

const ISSUE_CODE: Record<DemoFieldError, string> = {
  required: 'REQUIRED',
  tooShort: 'TOO_SHORT',
  tooLong: 'TOO_LONG',
  invalidPhone: 'INVALID_PHONE',
  invalidEmail: 'INVALID_EMAIL',
  invalidFleetSize: 'INVALID_FLEET_SIZE',
};

function validationFailed(fields: Array<{ field: string; code: string }>): ApiRequestError {
  return new ApiRequestError({ status: 400, code: 'VALIDATION_FAILED', message: 'The request is not valid', retryable: false, fields });
}

export function submitDemoRequest(body: DemoRequestBody, now = Date.now()): DemoRequestReceipt {
  const state = load(now);
  const recent = state.sent.filter((time) => now - time < MOCK_RATE_LIMIT.windowMs);
  if (recent.length >= MOCK_RATE_LIMIT.max) {
    const oldest = Math.min(...recent);
    throw new ApiRequestError({
      status: 429,
      code: 'RATE_LIMITED',
      message: 'Too many requests',
      retryable: true,
      retryAfterSeconds: Math.max(1, Math.ceil((oldest + MOCK_RATE_LIMIT.windowMs - now) / 1000)),
    });
  }

  const checked = validateDemoRequest(body);
  const issues = checked.ok ? [] : Object.entries(checked.errors).map(([field, error]) => ({ field, code: ISSUE_CODE[error] }));
  if (!isLocale(body.locale)) issues.push({ field: 'locale', code: 'INVALID' });
  if (typeof body.website !== 'string') issues.push({ field: 'website', code: 'INVALID' });
  if (typeof body.startedAt !== 'number' || !Number.isFinite(body.startedAt)) issues.push({ field: 'startedAt', code: 'INVALID' });
  if (issues.length > 0 || !checked.ok) throw validationFailed(issues);

  const sent = [...recent, now];
  // A filled honeypot is a bot: answer exactly as for a person, and keep nothing.
  if (body.website.trim() !== '') {
    save({ ...state, sent });
    return { status: 'RECEIVED' };
  }

  const sequence = state.sequence + 1;
  const createdAt = new Date(now).toISOString();
  const lead: Lead = {
    id: `lead-${sequence}-${now.toString(36)}`,
    reference: reference(sequence),
    ...checked.values,
    locale: body.locale,
    status: 'NEW',
    note: null,
    createdAt,
    updatedAt: createdAt,
  };
  save({ sequence, sent, leads: [lead, ...state.leads] });
  return { status: 'RECEIVED' };
}

export function listLeads({ status, cursor, limit = 25 }: LeadQuery, now = Date.now()): LeadPage {
  const matching = load(now)
    .leads.filter((lead) => !status || lead.status === status)
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const start = cursor ? Number(cursor) || 0 : 0;
  const items = matching.slice(start, start + limit);
  const next = start + limit;
  return { items, nextCursor: next < matching.length ? String(next) : null };
}

export function updateLead(leadId: string, body: LeadUpdate, now = Date.now()): Lead {
  const state = load(now);
  const existing = state.leads.find((lead) => lead.id === leadId);
  if (!existing) throw new ApiRequestError({ status: 404, code: 'NOT_FOUND', message: 'Lead not found', retryable: false });
  if (!(LEAD_STATUSES as readonly string[]).includes(body.status)) throw validationFailed([{ field: 'status', code: 'INVALID' }]);
  const note = body.note === undefined ? existing.note : body.note.trim() || null;
  if (note && note.length > NOTE_MAX) throw validationFailed([{ field: 'note', code: 'TOO_LONG' }]);
  const updated: Lead = { ...existing, status: body.status, note, updatedAt: new Date(now).toISOString() };
  save({ ...state, leads: state.leads.map((lead) => (lead.id === leadId ? updated : lead)) });
  return updated;
}
