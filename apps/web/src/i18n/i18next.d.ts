import 'i18next';
import type landing from './locales/en/landing.json';
import type legal from './locales/en/legal.json';
import type portal from './locales/en/portal.json';
import type site from './locales/en/site.json';
import type { resources } from './resources';

// Typed translation keys: t('status:transaction.completd') is a compile error.
// `landing`, `site` and `legal` are registered by the public pages' own chunks (src/landing/i18n.ts,
// src/public/i18n.ts, src/legal), and `portal` by the portal chunks (src/shell/i18n.ts), so their
// copy never weighs on the entry chunk; they are typed here like the others.
declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'common';
    resources: (typeof resources)['en'] & { landing: typeof landing; site: typeof site; legal: typeof legal; portal: typeof portal };
  }
}
