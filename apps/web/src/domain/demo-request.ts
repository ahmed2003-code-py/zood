/**
 * The "Request a demo" contract (POST /api/v1/public/demo-requests), shared by the landing form
 * and the in-browser mock API so both apply exactly the same rules as the server.
 */

/** Fleet-size buckets, in the order the form offers them. The pricing tiers use the same buckets. */
export const FLEET_SIZES = ['1-19', '20-49', '50-99', '100-249', '250+'] as const;
export type FleetSize = (typeof FLEET_SIZES)[number];

export const LEAD_STATUSES = ['NEW', 'CONTACTED', 'QUALIFIED', 'CLOSED'] as const;
export type LeadStatus = (typeof LEAD_STATUSES)[number];

export const DEMO_LIMITS = {
  companyName: { min: 2, max: 120 },
  contactName: { min: 2, max: 80 },
  email: { max: 254 },
} as const;

export type DemoField = 'companyName' | 'contactName' | 'phone' | 'email' | 'fleetSize';
export const DEMO_FIELDS: readonly DemoField[] = ['companyName', 'contactName', 'phone', 'email', 'fleetSize'];

export type DemoFieldError = 'required' | 'tooShort' | 'tooLong' | 'invalidPhone' | 'invalidEmail' | 'invalidFleetSize';

/** What the visitor typed. */
export interface DemoRequestInput {
  companyName: string;
  contactName: string;
  phone: string;
  email: string;
  fleetSize: FleetSize | '';
}

/** The cleaned values the API receives. */
export interface DemoRequestValues {
  companyName: string;
  contactName: string;
  /** Always the 11-digit national form, e.g. 01012345678. */
  phone: string;
  email: string;
  fleetSize: FleetSize;
}

export type DemoValidation =
  | { ok: true; values: DemoRequestValues }
  | { ok: false; errors: Partial<Record<DemoField, DemoFieldError>> };

const EASTERN_DIGITS = /[٠-٩۰-۹]/g;
const EGYPTIAN_MOBILE = /^01[0125]\d{8}$/;
// Deliberately simple: one @, no spaces, a dot in the domain. The server has the final word.
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@.]+$/;

/** Arabic-Indic (٠١٢) and Persian (۰۱۲) digits, typed on an Arabic keyboard, become 0-9. */
export function toWesternDigits(value: string): string {
  return value.replace(EASTERN_DIGITS, (digit) => String((digit.charCodeAt(0) - 0x0660) % 0x90 % 10));
}

/**
 * An Egyptian mobile number in its national form (01012345678), or undefined when it is not one.
 * Accepts spaces, dashes, dots and brackets, Arabic digits, and the +20 / 0020 / 20 prefixes.
 */
export function normalizeEgyptianMobile(input: string): string | undefined {
  let digits = toWesternDigits(input).replace(/[\s\-.()‎‏⁦-⁩]/g, '');
  if (digits.startsWith('+')) digits = digits.slice(1);
  else if (digits.startsWith('00')) digits = digits.slice(2);
  if (digits.startsWith('20') && digits.length === 12) digits = `0${digits.slice(2)}`;
  return EGYPTIAN_MOBILE.test(digits) ? digits : undefined;
}

export function isFleetSize(value: unknown): value is FleetSize {
  return typeof value === 'string' && (FLEET_SIZES as readonly string[]).includes(value);
}

function checkLength(value: string, limits: { min: number; max: number }): DemoFieldError | undefined {
  if (!value) return 'required';
  if (value.length < limits.min) return 'tooShort';
  if (value.length > limits.max) return 'tooLong';
  return undefined;
}

/** Collapses runs of whitespace and trims, so "  Nile   Transport " is stored as "Nile Transport". */
const tidy = (value: string) => value.replace(/\s+/g, ' ').trim();

/** Client-side validation with the server's limits. */
export function validateDemoRequest(input: DemoRequestInput): DemoValidation {
  const companyName = tidy(input.companyName);
  const contactName = tidy(input.contactName);
  const email = input.email.trim();
  const phone = normalizeEgyptianMobile(input.phone);
  const errors: Partial<Record<DemoField, DemoFieldError>> = {};

  const company = checkLength(companyName, DEMO_LIMITS.companyName);
  if (company) errors.companyName = company;
  const contact = checkLength(contactName, DEMO_LIMITS.contactName);
  if (contact) errors.contactName = contact;
  if (!input.phone.trim()) errors.phone = 'required';
  else if (!phone) errors.phone = 'invalidPhone';
  if (!email) errors.email = 'required';
  else if (email.length > DEMO_LIMITS.email.max) errors.email = 'tooLong';
  else if (!EMAIL.test(email)) errors.email = 'invalidEmail';
  if (!input.fleetSize) errors.fleetSize = 'required';
  else if (!isFleetSize(input.fleetSize)) errors.fleetSize = 'invalidFleetSize';

  if (Object.keys(errors).length > 0 || !phone || !isFleetSize(input.fleetSize)) return { ok: false, errors };
  return { ok: true, values: { companyName, contactName, phone, email, fleetSize: input.fleetSize } };
}
