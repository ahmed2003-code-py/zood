import { createBrowserRouter, Navigate } from 'react-router';
import { AppSplash, NotFound, RootLayout } from './RootLayout';

/**
 * Each experience is its own lazily loaded chunk (company, station, operations, POS, demo
 * launcher, design-system tooling), so opening one does not download the others.
 */
export const router = createBrowserRouter([
  {
    element: <RootLayout />,
    hydrateFallbackElement: <AppSplash />,
    children: [
      { index: true, element: <Navigate to="/demo" replace /> },
      { path: 'demo', lazy: () => import('@/demo/DemoLauncher').then((module) => ({ Component: module.DemoLauncher })) },
      { path: 'company/*', lazy: () => import('@/portals/company/CompanyApp').then((module) => ({ Component: module.CompanyApp })) },
      { path: 'station/*', lazy: () => import('@/portals/station/StationApp').then((module) => ({ Component: module.StationApp })) },
      { path: 'ops/*', lazy: () => import('@/portals/ops/OpsApp').then((module) => ({ Component: module.OpsApp })) },
      { path: 'receipt/:id', lazy: () => import('@/features/receipt/DigitalReceiptPage').then((module) => ({ Component: module.DigitalReceiptPage })) },
      { path: 'pos/*', lazy: () => import('@/pos/PosApp').then((module) => ({ Component: module.PosApp })) },
      { path: 'dev/ui', lazy: () => import('@/dev/DesignSystemPage').then((module) => ({ Component: module.DesignSystemPage })) },
      { path: 'dev/ui/canvas', lazy: () => import('@/dev/PreviewCanvas').then((module) => ({ Component: module.PreviewCanvas })) },
      { path: '*', element: <NotFound /> },
    ],
  },
]);
