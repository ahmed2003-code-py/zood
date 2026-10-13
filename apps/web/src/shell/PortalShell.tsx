import { useEffect, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocation } from 'react-router';
import { BottomNav } from './BottomNav';
import { DesktopHeader, MobileHeader } from './Headers';
import { NavRail, SidebarNav } from './Sidebar';
import type { PortalConfig } from './types';

/**
 * Responsive application shell shared by the Company, Station and Operations portals.
 *
 * - < 768px: minimal header + bottom navigation (4 destinations + More). No sidebar.
 * - 768–1023px: icon rail; the full grouped navigation opens in a drawer.
 * - >= 1024px: grouped sidebar.
 *
 * The sidebar is the first flex child and uses logical properties, so it sits on the right in
 * Arabic and on the left in English without any conditional code.
 */
export function PortalShell({ config, children }: { config: PortalConfig; children: ReactNode }) {
  const { t } = useTranslation('shell');
  const { pathname } = useLocation();

  useEffect(() => {
    window.scrollTo(0, 0);
  }, [pathname]);

  return (
    <div className="min-h-dvh md:flex">
      <a
        href="#main"
        className="sr-only z-50 rounded-control bg-primary px-4 py-2 text-primary-foreground focus:not-sr-only focus:fixed focus:start-4 focus:top-4"
      >
        {t('skipToContent')}
      </a>
      <aside data-shell="rail" className="sticky top-0 hidden h-dvh w-20 shrink-0 md:flex lg:hidden">
        <NavRail config={config} />
      </aside>
      <aside data-shell="sidebar" className="sticky top-0 hidden h-dvh w-64 shrink-0 lg:flex">
        <SidebarNav config={config} />
      </aside>
      <div className="flex min-w-0 flex-1 flex-col">
        <MobileHeader config={config} />
        <DesktopHeader config={config} />
        <main
          id="main"
          tabIndex={-1}
          className="mx-auto w-full max-w-6xl flex-1 px-4 pt-5 pb-[calc(5.5rem+env(safe-area-inset-bottom))] focus:outline-none md:px-6 md:pt-8 md:pb-12 lg:px-8"
        >
          {children}
        </main>
      </div>
      <BottomNav config={config} />
    </div>
  );
}
