import { describe, expect, it } from 'vitest';
import { scrubEvent, scrubUrl } from './observability';

describe('error reports carry no personal or financial data', () => {
  it('reduces a URL to its route pattern', () => {
    expect(scrubUrl('https://zawed.example/ops/companies/3f2c8a1e-1b2c-4d5e-8f90-123456789abc/wallet?plate=ABC%201234#x')).toBe(
      '/ops/companies/:id/wallet',
    );
    expect(scrubUrl('/receipt/TX-20261119-0042')).toBe('/receipt/:id');
    expect(scrubUrl('/company/vehicles')).toBe('/company/vehicles');
  });

  it('removes the request, the user, extras and breadcrumbs and keeps the route', () => {
    const event = {
      request: { url: 'https://x/ops?plate=1', headers: { Authorization: 'Bearer t' } },
      user: { email: 'someone@example.com' },
      extra: { amount: 500000 },
      breadcrumbs: [{ message: 'fetch /api/v1/ops/vehicles?plate=1' }],
      tags: { feature: 'import' },
      message: 'Something failed',
    };
    const sent = scrubEvent(event, '/ops/companies/:id/wallet');
    expect(sent.request).toBeUndefined();
    expect(sent.user).toBeUndefined();
    expect(sent.extra).toBeUndefined();
    expect(sent.breadcrumbs).toEqual([]);
    expect(sent.tags).toEqual({ feature: 'import', route: '/ops/companies/:id/wallet' });
    expect(sent.message).toBe('Something failed');
  });
});
