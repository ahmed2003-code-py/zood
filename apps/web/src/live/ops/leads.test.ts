import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { Lead } from '@/api/types';
import type { PagedResource } from '../use-resource';

// The screen renders on the server here (no DOM in these tests): give the app's browser-only
// modules (i18n start-up, storage) the few globals they read, in English.
vi.hoisted(() => {
  const values = new Map<string, string>();
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => void values.set(key, String(value)),
    removeItem: (key: string) => void values.delete(key),
    clear: () => values.clear(),
    key: () => null,
    length: 0,
  };
  const events = { addEventListener: () => undefined, removeEventListener: () => undefined };
  Object.assign(globalThis, {
    window: Object.assign(globalThis, events, { location: { search: '?lang=en' }, localStorage: storage, sessionStorage: storage }),
    localStorage: storage,
    document: { ...events, documentElement: { lang: '', dir: '' } },
  });
});

const { LeadsList } = await import('./LeadsPage');
const { leadQuery, leadUpdate, whatsappLink, LEAD_FILTERS } = await import('./leads-model');

const lead = (overrides: Partial<Lead> = {}): Lead => ({
  id: 'lead-1',
  reference: 'LD-00042',
  companyName: 'Nile Transport',
  contactName: 'Mona Adel',
  phone: '01012345678',
  email: 'mona@nile.example',
  fleetSize: '20-49',
  locale: 'en',
  status: 'NEW',
  note: null,
  createdAt: '2026-11-20T08:00:00Z',
  updatedAt: '2026-11-20T08:00:00Z',
  ...overrides,
});

function resource(state: Partial<PagedResource<Lead>>): PagedResource<Lead> {
  return { items: [], error: undefined, loading: false, loadingMore: false, hasMore: false, loadMore: () => undefined, reload: () => undefined, ...state };
}

const render = (state: Partial<PagedResource<Lead>>, filter: Parameters<typeof leadQuery>[0] = 'ALL') =>
  renderToStaticMarkup(createElement(LeadsList, { resource: resource(state), filter, onSelect: () => undefined }));

describe('Ops leads screen', () => {
  it('shows skeletons while loading', () => {
    expect(render({ loading: true })).toContain('aria-busy');
  });

  it('offers a retry when the list fails', () => {
    const html = render({ error: new Error('boom') });
    expect(html).toContain('Try again');
  });

  it('explains an empty list, and an empty filter differently', () => {
    expect(render({})).toContain('No demo requests yet');
    expect(render({}, 'CLOSED')).toContain('No requests with this status');
  });

  it('lists each request with its reference, company, fleet size and status', () => {
    const html = render({ items: [lead(), lead({ id: 'lead-2', reference: 'LD-00043', companyName: 'Delta Foods', status: 'QUALIFIED', fleetSize: '250+' })], hasMore: true });
    for (const text of ['LD-00042', 'Nile Transport', 'Mona Adel', '20–49 vehicles', 'New', 'Delta Foods', '250+ vehicles', 'Qualified', 'Load more']) {
      expect(html).toContain(text);
    }
  });
});

describe('leads model', () => {
  it('filters by status, or not at all', () => {
    expect(LEAD_FILTERS).toEqual(['ALL', 'NEW', 'CONTACTED', 'QUALIFIED', 'CLOSED']);
    expect(leadQuery('ALL', { limit: 25 })).toEqual({ limit: 25, status: undefined });
    expect(leadQuery('CONTACTED', { cursor: '25', limit: 25 })).toEqual({ cursor: '25', limit: 25, status: 'CONTACTED' });
  });

  it('sends only what changed, and nothing when nothing did', () => {
    const current = lead({ status: 'CONTACTED', note: 'Call Sunday' });
    expect(leadUpdate(current, 'CONTACTED', ' Call Sunday ')).toBeUndefined();
    expect(leadUpdate(current, 'QUALIFIED', 'Call Sunday')).toEqual({ status: 'QUALIFIED' });
    expect(leadUpdate(current, 'CONTACTED', 'Wants 30 vans')).toEqual({ status: 'CONTACTED', note: 'Wants 30 vans' });
    expect(leadUpdate(current, 'CLOSED', '')).toEqual({ status: 'CLOSED', note: '' });
  });

  it('builds a WhatsApp link from the national number', () => {
    expect(whatsappLink('01012345678')).toBe('https://wa.me/201012345678');
  });
});
