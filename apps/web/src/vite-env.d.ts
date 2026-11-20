/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** `mock` (default): in-browser demo data. `http`: the ZAWED API. */
  readonly VITE_API_MODE?: 'mock' | 'http';
  /** Base URL of the API. Defaults to the same origin (`/api/v1`). */
  readonly VITE_API_BASE_URL?: string;
  /** Sentry DSN. Error reporting is off when it is unset (src/lib/observability.ts). */
  readonly VITE_SENTRY_DSN?: string;
  /** Sentry environment name, e.g. `dev`. Defaults to the Vite mode. */
  readonly VITE_SENTRY_ENVIRONMENT?: string;
  /** The release the build belongs to, e.g. the commit. */
  readonly VITE_SENTRY_RELEASE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
