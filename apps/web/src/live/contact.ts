/**
 * Egyptian contact numbers, checked in the form the way the API checks them
 * (`platform/text/EgyptianContact.java`), so a typo is caught before the request. The API stays the
 * authority: it cleans and validates again.
 */
import { normalizeDigits } from './input';

const SEPARATORS = /[\s\u00a0\u2000-\u200b\u2010-\u2015\-.()]+/g;
const MOBILE = /^01[0125][0-9]{8}$/;
const PHONE = /^(01[0125][0-9]{8}|02[0-9]{8}|03[0-9]{7}|01[35][0-9]{7}|0[4-9][0-9]{8})$/;
const TAX_ID = /^[0-9]{9}$/;
const COMMERCIAL_REGISTRATION = /^[0-9]{3,12}$/;

const compact = (value: string) => normalizeDigits(value).replace(SEPARATORS, '');

/** A phone as the API stores it: +20 / 0020 / 20 before a mobile become the leading 0. */
export function cleanPhone(value: string): string {
  let text = compact(value);
  if (text.startsWith('+20')) text = `0${text.slice(3)}`;
  else if (text.startsWith('0020')) text = `0${text.slice(4)}`;
  else if (text.length === 12 && text.startsWith('201')) text = `0${text.slice(2)}`;
  else if (text.length === 10 && text.startsWith('1')) text = `0${text}`;
  return text;
}

/** An empty value is valid here: whether a field is required is the form's call. */
export const isEgyptianPhone = (value: string) => !value.trim() || PHONE.test(cleanPhone(value));
export const isEgyptianMobile = (value: string) => !value.trim() || MOBILE.test(cleanPhone(value));
export const isTaxId = (value: string) => !value.trim() || TAX_ID.test(compact(value));
export const isCommercialRegistration = (value: string) => !value.trim() || COMMERCIAL_REGISTRATION.test(compact(value));
