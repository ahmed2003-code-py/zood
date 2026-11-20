/**
 * Temporary public contact details, from the owner (2026-11-20). Replace them here only.
 * The e-mail is zawed-fuel (f-u-e-l); "zawed-fual" was a typo and must never appear.
 */
export const CONTACT = {
  /** Shown as typed in Egypt. */
  whatsappDisplay: '01002589923',
  whatsappUrl: 'https://wa.me/201002589923',
  email: 'zawed-fuel@gmail.com',
} as const;

export const mailtoUrl = (subject?: string) =>
  `mailto:${CONTACT.email}${subject ? `?subject=${encodeURIComponent(subject)}` : ''}`;
