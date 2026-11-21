import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { readStorage, readUrlParam, STORAGE_KEYS, writeStorage } from '@/lib/storage';
import { isThemePreference, ThemeContext, type ResolvedTheme, type ThemePreference } from './theme-context';

const DARK_QUERY = '(prefers-color-scheme: dark)';

function subscribeToSystemTheme(onChange: () => void): () => void {
  const media = window.matchMedia(DARK_QUERY);
  media.addEventListener('change', onChange);
  return () => media.removeEventListener('change', onChange);
}

/** True while a View Transition is running on the page (`:active-view-transition`, where supported). */
function viewTransitionActive(root: HTMLElement): boolean {
  try {
    return root.matches(':active-view-transition');
  } catch {
    return false;
  }
}

function getSystemTheme(): ResolvedTheme {
  return window.matchMedia(DARK_QUERY).matches ? 'dark' : 'light';
}

interface ThemeProviderProps {
  children: ReactNode;
  /** Storage key for the preference. The POS uses its own key so it can default to Light. */
  storageKey?: string;
  defaultPreference?: ThemePreference;
  /**
   * scope: theme only this subtree (renders a wrapper element carrying data-theme) instead of <html>.
   * Used by the POS, which keeps its own theme inside the portal page.
   */
  scope?: boolean;
  /** Classes for the scope wrapper element. */
  className?: string;
  /** data-surface for the scope wrapper (e.g. "pos" enables the POS size and contrast tokens). */
  surface?: string;
  /** Controlled mode: the preference is owned by the caller (e.g. the shared POS theme store). */
  preference?: ThemePreference;
  onPreferenceChange?: (preference: ThemePreference) => void;
}

/**
 * Preference order: ?theme= (non-persisted override, used by previews; page scope only) >
 * saved preference > default. "system" follows the OS setting live.
 */
export function ThemeProvider({
  children,
  storageKey = STORAGE_KEYS.theme,
  defaultPreference = 'system',
  scope = false,
  className,
  surface,
  preference: controlledPreference,
  onPreferenceChange,
}: ThemeProviderProps) {
  const controlled = controlledPreference !== undefined;
  const [overridden] = useState(() => !scope && isThemePreference(readUrlParam('theme')));
  const [preference, setPreferenceState] = useState<ThemePreference>(() => {
    const fromUrl = readUrlParam('theme');
    if (!scope && isThemePreference(fromUrl)) return fromUrl;
    const stored = readStorage(storageKey);
    return isThemePreference(stored) ? stored : defaultPreference;
  });

  const systemTheme = useSyncExternalStore(subscribeToSystemTheme, getSystemTheme, () => 'light' as const);
  const effective = controlled ? controlledPreference : preference;
  const resolved: ResolvedTheme = effective === 'system' ? systemTheme : effective;

  const applied = useRef<ResolvedTheme | undefined>(undefined);
  useEffect(() => {
    if (scope) return;
    const root = document.documentElement;
    // A real switch (not the first paint) cross-fades the colours for 200 ms; see index.css.
    // A View Transition (the public pages' theme button) already cross-fades the whole page; adding
    // colour transitions inside it would fade twice. The public pages' own fallback fade steps aside
    // while data-theme-switching is on (src/public/stage.css), so only one fade ever runs.
    const switching = applied.current !== undefined && applied.current !== resolved && !viewTransitionActive(root);
    applied.current = resolved;
    if (switching) root.dataset.themeSwitching = '';
    root.dataset.theme = resolved;
    root.style.colorScheme = resolved;
    if (!switching) return;
    const timer = window.setTimeout(() => delete root.dataset.themeSwitching, 260);
    return () => {
      window.clearTimeout(timer);
      delete root.dataset.themeSwitching;
    };
  }, [resolved, scope]);

  // Keep other open windows of the demo on the same theme.
  useEffect(() => {
    if (overridden || controlled) return;
    const onStorage = (event: StorageEvent) => {
      if (event.key === storageKey && isThemePreference(event.newValue)) setPreferenceState(event.newValue);
    };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, [overridden, controlled, storageKey]);

  const setPreference = useCallback(
    (next: ThemePreference) => {
      if (onPreferenceChange) return onPreferenceChange(next);
      setPreferenceState(next);
      if (!overridden) writeStorage(storageKey, next);
    },
    [onPreferenceChange, overridden, storageKey],
  );

  const value = useMemo(() => ({ preference: effective, resolved, setPreference }), [effective, resolved, setPreference]);
  return (
    <ThemeContext.Provider value={value}>
      {scope ? (
        <div data-theme={resolved} data-surface={surface} style={{ colorScheme: resolved }} className={cn('bg-background text-foreground', className)}>
          {children}
        </div>
      ) : (
        children
      )}
    </ThemeContext.Provider>
  );
}
