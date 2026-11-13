import { useEffect, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, Navigate, useLocation } from 'react-router';
import { API_MODE } from '@/api';
import { AppSplash } from '@/app/RootLayout';
import { Button } from '@/design-system';
import { AuthLayout } from './AuthLayout';
import { HOME_BY_ROLE, loginPath, PORTAL_ROLES, type PortalId } from './portals';
import { useSession } from './session-store';

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
  const { status, user, door, restore } = useSession();

  useEffect(() => {
    void restore();
  }, [restore]);

  if (status === 'unknown') return <AppSplash />;

  const here = `${location.pathname}${location.search}`;
  if (status === 'anonymous' || !user) {
    return <Navigate to={`${loginPath(door)}?next=${encodeURIComponent(here)}`} replace />;
  }
  if (user.mustChangePassword) {
    return <Navigate to={`/account/password?next=${encodeURIComponent(here)}`} replace />;
  }
  if (!PORTAL_ROLES[portal].includes(user.role)) return <NoAccess />;
  return <>{children}</>;
}

function NoAccess() {
  const { t } = useTranslation('auth');
  const { user, signOut, door } = useSession();
  if (!user) return null;
  return (
    <AuthLayout
      title={t('noAccess.title')}
      description={t('noAccess.description', { name: user.fullName, role: t(`roles.${user.role}`) })}
    >
      <div className="flex flex-col gap-2">
        <Button asChild size="lg" fullWidth>
          <Link to={HOME_BY_ROLE[user.role]}>{t('noAccess.goHome')}</Link>
        </Button>
        <Button variant="secondary" fullWidth onClick={() => void signOut().then(() => window.location.assign(loginPath(door)))}>
          {t('noAccess.switchAccount')}
        </Button>
      </div>
    </AuthLayout>
  );
}
