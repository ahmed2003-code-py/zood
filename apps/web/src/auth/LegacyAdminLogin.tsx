import { Navigate, useLocation } from 'react-router';
import { legacyAdminLoginTarget } from './portals';

/**
 * The Super Admin's former sign-in page. Since the owner's decision of 2026-11-20 (ADR 0008
 * amendment) every role signs in at /login, so an old bookmark lands there, keeping `?next=`.
 */
export function LegacyAdminLogin() {
  const { search } = useLocation();
  return <Navigate to={legacyAdminLoginTarget(search)} replace />;
}
