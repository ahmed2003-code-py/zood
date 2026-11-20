import { describe, expect, it } from 'vitest';
import { normalizeEgyptianMobile, toWesternDigits, validateDemoRequest, type DemoRequestInput } from './demo-request';

const valid: DemoRequestInput = {
  companyName: 'Nile Transport',
  contactName: 'Mona Adel',
  phone: '01012345678',
  email: 'mona@nile.example',
  fleetSize: '20-49',
};

describe('normalizeEgyptianMobile', () => {
  it.each([
    ['01012345678', '01012345678'],
    ['0111 234 5678', '01112345678'],
    ['012-3456-7890', '01234567890'],
    ['+20 155 123 4567', '01551234567'],
    ['00201012345678', '01012345678'],
    ['201012345678', '01012345678'],
    ['(010) 1234.5678', '01012345678'],
    ['٠١٠١٢٣٤٥٦٧٨', '01012345678'],
    ['۰۱۱۱۲۳۴۵۶۷۸', '01112345678'],
  ])('accepts %s', (input, expected) => {
    expect(normalizeEgyptianMobile(input)).toBe(expected);
  });

  it.each([
    ['01312345678', 'an unknown operator prefix'],
    ['0101234567', 'ten digits'],
    ['010123456789', 'twelve digits'],
    ['0225551234', 'a Cairo landline'],
    ['+44 7700 900123', 'a foreign number'],
    ['01O12345678', 'a letter O'],
    ['', 'nothing'],
  ])('rejects %s (%s)', (input) => {
    expect(normalizeEgyptianMobile(input)).toBeUndefined();
  });

  it('turns Arabic-Indic and Persian digits into Western digits', () => {
    expect(toWesternDigits('٠١٢٣٤٥٦٧٨٩ ۰۱۲۳۴۵۶۷۸۹')).toBe('0123456789 0123456789');
  });
});

describe('validateDemoRequest', () => {
  it('cleans valid input: tidy names, trimmed e-mail, national phone form', () => {
    const result = validateDemoRequest({ ...valid, companyName: '  Nile   Transport ', phone: '+20 101 234 5678', email: ' mona@nile.example ' });
    expect(result).toEqual({
      ok: true,
      values: { companyName: 'Nile Transport', contactName: 'Mona Adel', phone: '01012345678', email: 'mona@nile.example', fleetSize: '20-49' },
    });
  });

  it('reports every empty field as required', () => {
    const result = validateDemoRequest({ companyName: ' ', contactName: '', phone: '', email: '', fleetSize: '' });
    expect(result).toEqual({
      ok: false,
      errors: { companyName: 'required', contactName: 'required', phone: 'required', email: 'required', fleetSize: 'required' },
    });
  });

  it('applies the server length limits', () => {
    const short = validateDemoRequest({ ...valid, companyName: 'A', contactName: 'B' });
    expect(short).toMatchObject({ ok: false, errors: { companyName: 'tooShort', contactName: 'tooShort' } });
    const long = validateDemoRequest({ ...valid, companyName: 'x'.repeat(121), contactName: 'y'.repeat(81), email: `${'a'.repeat(250)}@b.eg` });
    expect(long).toMatchObject({ ok: false, errors: { companyName: 'tooLong', contactName: 'tooLong', email: 'tooLong' } });
    expect(validateDemoRequest({ ...valid, companyName: 'x'.repeat(120), contactName: 'y'.repeat(80) }).ok).toBe(true);
  });

  it('rejects a malformed phone, e-mail or fleet size', () => {
    const result = validateDemoRequest({ ...valid, phone: '0101234', email: 'mona@nile', fleetSize: '10-20' as DemoRequestInput['fleetSize'] });
    expect(result).toEqual({ ok: false, errors: { phone: 'invalidPhone', email: 'invalidEmail', fleetSize: 'invalidFleetSize' } });
  });

  it('accepts Arabic names as typed', () => {
    expect(validateDemoRequest({ ...valid, companyName: 'النور للنقل', contactName: 'محمود' }).ok).toBe(true);
  });
});
