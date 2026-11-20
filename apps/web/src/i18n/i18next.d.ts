import 'i18next';
import type landing from './locales/en/landing.json';
import type { resources } from './resources';

// Typed translation keys: t('status:transaction.completd') is a compile error.
// `landing` is registered by the landing page's own chunk (src/landing/i18n.ts), so its copy never
// weighs on the portals' entry chunk; it is typed here like the others.
declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'common';
    resources: (typeof resources)['en'] & { landing: typeof landing };
  }
}
