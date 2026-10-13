import { create } from 'zustand';
import { readStorage, STORAGE_KEYS, writeStorage } from '@/lib/storage';
import { isThemePreference, type ThemePreference } from './theme-context';

interface PosThemeState {
  preference: ThemePreference;
  setPreference: (preference: ThemePreference) => void;
}

/**
 * The POS theme preference, separate from the portals' theme. Light is the default because the
 * terminal is used outdoors. Shared so both the POS agent menu and the presenter panel can change it.
 */
export const usePosTheme = create<PosThemeState>((set) => {
  const stored = readStorage(STORAGE_KEYS.posTheme);
  return {
    preference: isThemePreference(stored) ? stored : 'light',
    setPreference: (preference) => {
      writeStorage(STORAGE_KEYS.posTheme, preference);
      set({ preference });
    },
  };
});

if (typeof window !== 'undefined') {
  window.addEventListener('storage', (event) => {
    if (event.key === STORAGE_KEYS.posTheme && isThemePreference(event.newValue)) {
      usePosTheme.setState({ preference: event.newValue });
    }
  });
}
