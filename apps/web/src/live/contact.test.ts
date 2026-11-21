import { describe, expect, it } from 'vitest';
import { cleanPhone, isCommercialRegistration, isEgyptianMobile, isEgyptianPhone, isTaxId } from './contact';

describe('Egyptian contact numbers (same rules as the API)', () => {
  it('cleans the forms people type', () => {
    expect(cleanPhone('+20 100 123 4567')).toBe('01001234567');
    expect(cleanPhone('0020-100-123-4567')).toBe('01001234567');
    expect(cleanPhone('١٠٠١٢٣٤٥٦٧')).toBe('01001234567');
  });

  it('accepts mobiles and landlines, refuses the rest', () => {
    expect(isEgyptianPhone('01001234567')).toBe(true);
    expect(isEgyptianPhone('02 2345 6789')).toBe(true);
    expect(isEgyptianPhone('03 456 7890')).toBe(true);
    expect(isEgyptianPhone('0100123')).toBe(false);
    expect(isEgyptianPhone('01301234567')).toBe(false);
    expect(isEgyptianMobile('0223456789')).toBe(false);
    expect(isEgyptianPhone('')).toBe(true);
  });

  it('checks the tax id and commercial registration shapes', () => {
    expect(isTaxId('123-456-789')).toBe(true);
    expect(isTaxId('12345678')).toBe(false);
    expect(isCommercialRegistration('10240')).toBe(true);
    expect(isCommercialRegistration('CR-10240')).toBe(false);
  });
});
