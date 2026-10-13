/**
 * Safe access to browser storage. Storage can be unavailable (private mode, blocked site data,
 * embedded previews), so every read and write is guarded and failures fall back to defaults.
 */
export function readStorage(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function writeStorage(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Storage unavailable: the preference lasts for this session only.
  }
}

/** URL query parameter, used for non-persisted overrides such as ?lang=en&theme=dark. */
export function readUrlParam(name: string): string | null {
  try {
    return new URLSearchParams(window.location.search).get(name);
  } catch {
    return null;
  }
}

export const STORAGE_KEYS = {
  locale: 'zood.locale',
  theme: 'zood.theme',
  posTheme: 'zood.pos.theme',
  demoState: 'zood.demo.v1',
  devViewport: 'zood.dev.viewport',
} as const;
