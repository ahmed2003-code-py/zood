import type { ComponentType } from 'react';
import { createBrowserRouter, Navigate } from 'react-router';
import { AppSplash, NotFound, RootLayout, RouteError } from './RootLayout';

const RELOAD_FLAG = 'zood.chunk-reload';

/**
 * Lazily loads a route chunk. If the chunk fails to load (e.g. a dropped or refused request, or a
 * stale chunk after a redeploy), the page reloads once, which clears the browser's cache of failed
 * modules. A second failure shows RouteError with a Reload button.
 */
function lazyRoute<T>(load: () => Promise<T>, pick: (module: T) => ComponentType) {
  return async () => {
    try {
      const module = await load();
      sessionStorage.removeItem(RELOAD_FLAG);
      return { Component: pick(module) };
    } catch (error) {
      if (!sessionStorage.getItem(RELOAD_FLAG)) {
        sessionStorage.setItem(RELOAD_FLAG, '1');
        window.location.reload();
        return new Promise<never>(() => undefined);
      }
      sessionStorage.removeItem(RELOAD_FLAG);
      throw error;
    }
  };
}

/**
 * Each experience is its own lazily loaded chunk (company, station, operations, POS, demo
 * launcher, design-system tooling), so opening one does not download the others.
 */
export const router = createBrowserRouter([
  {
    element: <RootLayout />,
    errorElement: <RouteError />,
    hydrateFallbackElement: <AppSplash />,
    children: [
      { index: true, element: <Navigate to="/demo" replace /> },
      { path: 'demo', lazy: lazyRoute(() => import('@/demo/DemoLauncher'), (module) => module.DemoLauncher) },
      { path: 'company/*', lazy: lazyRoute(() => import('@/portals/company/CompanyApp'), (module) => module.CompanyApp) },
      { path: 'station/*', lazy: lazyRoute(() => import('@/portals/station/StationApp'), (module) => module.StationApp) },
      { path: 'ops/*', lazy: lazyRoute(() => import('@/portals/ops/OpsApp'), (module) => module.OpsApp) },
      { path: 'receipt/:id', lazy: lazyRoute(() => import('@/features/receipt/DigitalReceiptPage'), (module) => module.DigitalReceiptPage) },
      { path: 'pos/*', lazy: lazyRoute(() => import('@/pos/PosApp'), (module) => module.PosApp) },
      { path: 'dev/ui', lazy: lazyRoute(() => import('@/dev/DesignSystemPage'), (module) => module.DesignSystemPage) },
      { path: 'dev/ui/canvas', lazy: lazyRoute(() => import('@/dev/PreviewCanvas'), (module) => module.PreviewCanvas) },
      { path: '*', element: <NotFound /> },
    ],
  },
]);
