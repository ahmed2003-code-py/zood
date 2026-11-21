import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button } from '@/design-system';
import { AuthLayout } from './AuthLayout';
import { HOME_BY_ROLE, LOGIN_PATH } from './portals';
import { useSession } from './session-store';

/** A signed-in user opened a portal their role does not use. */
export default function NoAccess() {
  const { t } = useTranslation('auth');
  const { user, signOut } = useSession();
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
        <Button variant="secondary" fullWidth onClick={() => void signOut().then(() => window.location.assign(LOGIN_PATH))}>
          {t('noAccess.switchAccount')}
        </Button>
      </div>
    </AuthLayout>
  );
}
