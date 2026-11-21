/**
 * Starts loading the portal a successful sign-in is about to open, while the welcome plays, so the
 * portal is ready when the moment ends. The same modules the router loads lazily (app/router.tsx).
 */
const PORTALS: Array<[prefix: string, load: () => Promise<unknown>]> = [
  ['/company', () => import('@/portals/company/CompanyApp')],
  ['/station', () => import('@/portals/station/StationApp')],
  ['/ops', () => import('@/portals/ops/OpsApp')],
  ['/account/password', () => import('@/auth/ChangePasswordPage')],
];

export function preloadDestination(path: string): void {
  const match = PORTALS.find(([prefix]) => path === prefix || path.startsWith(`${prefix}/`) || path.startsWith(`${prefix}?`));
  // A failed preload is harmless: the router loads (and retries) the chunk itself.
  match?.[1]().catch(() => undefined);
}
