import { beforeEach, describe, expect, it } from 'vitest';
import { ApiRequestError } from '@/api/http-client';
import type { DemoRequestBody } from '@/api/types';
import { listLeads, MOCK_RATE_LIMIT, resetMockLeads, submitDemoRequest, updateLead } from './leads';

const NOW = Date.parse('2026-11-20T10:00:00Z');

const body = (overrides: Partial<DemoRequestBody> = {}): DemoRequestBody => ({
  companyName: 'Nile Transport',
  contactName: 'Mona Adel',
  phone: '+20 101 234 5678',
  email: 'mona@nile.example',
  fleetSize: '20-49',
  locale: 'en',
  website: '',
  startedAt: NOW - 20_000,
  ...overrides,
});

function thrown(run: () => unknown): ApiRequestError {
  try {
    run();
  } catch (error) {
    expect(error).toBeInstanceOf(ApiRequestError);
    return error as ApiRequestError;
  }
  throw new Error('expected a refusal');
}

beforeEach(() => resetMockLeads());

describe('mock demo requests', () => {
  it('stores a request as a NEW lead, newest first, with the phone normalised', () => {
    const before = listLeads({}, NOW).items.length;
    expect(submitDemoRequest(body(), NOW)).toEqual({ status: 'RECEIVED' });
    const { items } = listLeads({}, NOW);
    expect(items).toHaveLength(before + 1);
    expect(items[0]).toMatchObject({ companyName: 'Nile Transport', phone: '01012345678', status: 'NEW', locale: 'en', note: null });
    expect(items[0]?.reference).toMatch(/^LD-\d{5}$/);
  });

  it('answers a filled honeypot like a success and keeps nothing', () => {
    const before = listLeads({}, NOW).items.length;
    expect(submitDemoRequest(body({ website: 'https://spam.example' }), NOW)).toEqual({ status: 'RECEIVED' });
    expect(listLeads({}, NOW).items).toHaveLength(before);
  });

  it('refuses invalid input with VALIDATION_FAILED and the fields', () => {
    const error = thrown(() => submitDemoRequest(body({ phone: '123', email: 'nope', locale: 'fr' as 'en' }), NOW));
    expect(error.status).toBe(400);
    expect(error.code).toBe('VALIDATION_FAILED');
    expect(error.fields?.map((issue) => issue.field).sort()).toEqual(['email', 'locale', 'phone']);
  });

  it('rate limits a browser that sends too many requests, with a retry time', () => {
    for (let index = 0; index < MOCK_RATE_LIMIT.max; index++) submitDemoRequest(body(), NOW + index);
    const error = thrown(() => submitDemoRequest(body(), NOW + 10));
    expect(error.status).toBe(429);
    expect(error.code).toBe('RATE_LIMITED');
    expect(error.retryAfterSeconds).toBe(600);
    expect(submitDemoRequest(body(), NOW + MOCK_RATE_LIMIT.windowMs + 1)).toEqual({ status: 'RECEIVED' });
  });
});

describe('mock leads', () => {
  it('filters by status and pages with a cursor', () => {
    for (let index = 0; index < 4; index++) submitDemoRequest(body({ companyName: `Company ${index}` }), NOW + index);
    const fresh = listLeads({ status: 'NEW', limit: 3 }, NOW);
    expect(fresh.items.every((lead) => lead.status === 'NEW')).toBe(true);
    expect(fresh.items[0]?.companyName).toBe('Company 3');
    expect(fresh.nextCursor).toBeTruthy();
    const rest = listLeads({ status: 'NEW', limit: 3, cursor: fresh.nextCursor ?? undefined }, NOW);
    expect(rest.nextCursor).toBeNull();
    expect(new Set([...fresh.items, ...rest.items].map((lead) => lead.id)).size).toBe(fresh.items.length + rest.items.length);
    expect(listLeads({ status: 'CLOSED' }, NOW).items).toEqual([]);
  });

  it('changes the status and the note, and refuses an unknown lead', () => {
    const lead = listLeads({}, NOW).items[0]!;
    const updated = updateLead(lead.id, { status: 'CONTACTED', note: '  Called, wants a demo on Sunday  ' }, NOW + 1000);
    expect(updated).toMatchObject({ status: 'CONTACTED', note: 'Called, wants a demo on Sunday', updatedAt: new Date(NOW + 1000).toISOString() });
    expect(listLeads({ status: 'CONTACTED' }, NOW).items.map((item) => item.id)).toContain(lead.id);
    expect(updateLead(lead.id, { status: 'CLOSED' }, NOW).note).toBe('Called, wants a demo on Sunday');
    expect(thrown(() => updateLead('missing', { status: 'CLOSED' }, NOW)).code).toBe('NOT_FOUND');
    expect(thrown(() => updateLead(lead.id, { status: 'WON' as 'NEW' }, NOW)).code).toBe('VALIDATION_FAILED');
  });
});
