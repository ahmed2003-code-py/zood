import { lazy, Suspense, useEffect, type ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { API_MODE } from '@/api';
import { AppSplash } from '@/app/RootLayout';
import { LOGIN_PATH, PORTAL_ROLES, type PortalId } from './portals';
import { useSession } from './session-store';

// Rare, and it carries the public sign-in frame: loaded only when needed, so portals stay light.
const NoAccess = lazy(() => import('./NoAccess'));

/**
 * Lets a signed-in user with a matching role into a portal.
 *
 * In demo mode (VITE_API_MODE=mock) there is no sign-in and every portal opens. Against the API,
 * this only decides where to send people: the API refuses anything their role does not allow.
 */
export function RequirePortal({ portal, children }: { portal: PortalId; children: ReactNode }) {
  if (API_MODE === 'mock') return <>{children}</>;
  return <Guard portal={portal}>{children}</Guard>;
}

function Guard({ portal, children }: { portal: PortalId; children: ReactNode }) {
  const location = useLocation();
  const { status, user, restore } = useSession();

  useEffect(() => {
    void restore();
  }, [restore]);

  if (status === 'unknown') return <AppSplash />;

  const here = `${location.pathname}${location.search}`;
  if (status === 'anonymous' || !user) {
    return <Navigate to={`${LOGIN_PATH}?next=${encodeURIComponent(here)}`} replace />;
  }
  if (user.mustChangePassword) {
    return <Navigate to={`/account/password?next=${encodeURIComponent(here)}`} replace />;
  }
  if (!PORTAL_ROLES[portal].includes(user.role)) {
    return (
      <Suspense fallback={<AppSplash />}>
        <NoAccess />
      </Suspense>
    );
  }
  return <>{children}</>;
}
