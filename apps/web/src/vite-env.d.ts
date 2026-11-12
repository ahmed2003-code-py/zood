/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** `mock` (default): in-browser demo data. `http`: the ZAWED API. */
  readonly VITE_API_MODE?: 'mock' | 'http';
  /** Base URL of the API. Defaults to the same origin (`/api/v1`). */
  readonly VITE_API_BASE_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
