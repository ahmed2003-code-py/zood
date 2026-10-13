import { Direction } from 'radix-ui';
import type { ReactNode } from 'react';
import { Toaster } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { ThemeProvider } from '@/theme/ThemeProvider';

function DirectionBoundary({ children }: { children: ReactNode }) {
  const { dir } = useLocale();
  return <Direction.Provider dir={dir}>{children}</Direction.Provider>;
}

export function AppProviders({ children }: { children: ReactNode }) {
  return (
    <ThemeProvider>
      <DirectionBoundary>
        {children}
        <Toaster />
      </DirectionBoundary>
    </ThemeProvider>
  );
}
