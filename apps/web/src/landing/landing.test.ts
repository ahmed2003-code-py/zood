import { describe, expect, it } from 'vitest';
import { ApiRequestError, NETWORK_ERROR } from '@/api/http-client';
import { FLEET_SIZES } from '@/domain/demo-request';
import ar from '@/i18n/locales/ar/landing.json';
import en from '@/i18n/locales/en/landing.json';
import { CONTACT, mailtoUrl } from './contact';
import { buildDemoRequest, describeSendFailure } from './demo-form-model';
import { PRICES_ARE_PLACEHOLDERS, PRICING_FLEET_SIZES, PRICING_TIERS, TAG_PRICE_PER_VEHICLE, tierPrice, YEARLY_BILLED_MONTHS } from './pricing';

const values = { companyName: 'Nile Transport', contactName: 'Mona', phone: '01012345678', email: 'mona@nile.example', fleetSize: '1-19' as const };

describe('demo request body', () => {
  it('sends the honeypot as found and the render time', () => {
    expect(buildDemoRequest(values, 'ar', { website: '', startedAt: 1_700_000_000_000 })).toEqual({
      ...values,
      locale: 'ar',
      website: '',
      startedAt: 1_700_000_000_000,
    });
    // A bot that filled the hidden field is not stopped in the browser: the server decides, silently.
    expect(buildDemoRequest(values, 'en', { website: 'http://spam.example', startedAt: 1 }).website).toBe('http://spam.example');
  });
});

describe('describeSendFailure', () => {
  const failure = (init: Partial<ConstructorParameters<typeof ApiRequestError>[0]>) =>
    new ApiRequestError({ status: 400, code: 'X', message: 'x', retryable: false, ...init });

  it('explains a rate limit with the wait rounded up to minutes', () => {
    expect(describeSendFailure(failure({ status: 429, code: 'RATE_LIMITED', retryAfterSeconds: 61 }))).toEqual({ kind: 'rateLimited', retryMinutes: 2 });
    expect(describeSendFailure(failure({ status: 429, code: 'RATE_LIMITED' }))).toEqual({ kind: 'rateLimited', retryMinutes: undefined });
  });

  it('names the form fields the server blamed and ignores the others', () => {
    const error = failure({ code: 'VALIDATION_FAILED', field: 'phone', fields: [{ field: 'phone', code: 'X' }, { field: 'email', code: 'X' }, { field: 'startedAt', code: 'X' }] });
    expect(describeSendFailure(error)).toEqual({ kind: 'validation', fields: ['phone', 'email'] });
  });

  it('tells a network failure from a server failure', () => {
    expect(describeSendFailure(failure({ status: 0, code: NETWORK_ERROR, retryable: true }))).toEqual({ kind: 'network' });
    expect(describeSendFailure(failure({ status: 500, code: 'INTERNAL', requestId: 'req-7' }))).toEqual({ kind: 'generic', requestId: 'req-7' });
    expect(describeSendFailure(new Error('boom'))).toEqual({ kind: 'generic' });
  });
});

describe('pricing config', () => {
  it('is marked as placeholder until the owner sets real prices', () => {
    expect(PRICES_ARE_PLACEHOLDERS).toBe(true);
  });

  it('uses the demo form fleet-size buckets, in order', () => {
    expect(PRICING_FLEET_SIZES).toEqual(FLEET_SIZES);
  });

  it('keeps every amount in whole piastres, cheaper per vehicle as the fleet grows', () => {
    const priced = PRICING_TIERS.flatMap((tier) => (tier.monthlyPerVehicle === undefined ? [] : [tier.monthlyPerVehicle]));
    for (const amount of [...priced, TAG_PRICE_PER_VEHICLE]) {
      expect(Number.isInteger(amount)).toBe(true);
      expect(amount).toBeGreaterThan(0);
    }
    expect([...priced].sort((a, b) => b - a)).toEqual(priced);
    expect(PRICING_TIERS.at(-1)?.monthlyPerVehicle).toBeUndefined();
  });

  it('bills a year as fewer than twelve months', () => {
    const tier = PRICING_TIERS[0]!;
    expect(YEARLY_BILLED_MONTHS).toBeLessThan(12);
    expect(tierPrice(tier, 'monthly')).toBe(tier.monthlyPerVehicle);
    expect(tierPrice(tier, 'yearly')).toBe((tier.monthlyPerVehicle ?? 0) * YEARLY_BILLED_MONTHS);
    expect(tierPrice(PRICING_TIERS.at(-1)!, 'yearly')).toBeUndefined();
  });

  it('has a name and a description for every tier in both languages', () => {
    for (const tier of PRICING_TIERS) {
      expect(ar.pricing.tiers[tier.id].name).toBeTruthy();
      expect(en.pricing.tiers[tier.id].name).toBeTruthy();
    }
  });
});

describe('contact details', () => {
  it('uses the confirmed e-mail and WhatsApp number', () => {
    expect(CONTACT.email).toBe('zawed-fuel@gmail.com');
    expect(CONTACT.whatsappUrl).toBe('https://wa.me/201002589923');
    expect(mailtoUrl('Demo')).toBe('mailto:zawed-fuel@gmail.com?subject=Demo');
  });

  it('never carries the misspelt address', () => {
    expect(JSON.stringify([ar, en, CONTACT])).not.toContain('zawed-fual');
  });
});
