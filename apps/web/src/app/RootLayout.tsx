import { useTranslation } from 'react-i18next';
import { Link, Outlet, useNavigation } from 'react-router';
import { Button, ZoodWordmark } from '@/design-system';
import { PresenterHost } from '@/demo/presenter/PresenterHost';

/** Wraps every route: page outlet, route-loading indicator and the hidden presenter tooling. */
export function RootLayout() {
  const navigation = useNavigation();
  return (
    <>
      {navigation.state === 'loading' && (
        <div aria-hidden className="fixed inset-x-0 top-0 z-[70] h-0.5 animate-pulse bg-accent" />
      )}
      <Outlet />
      <PresenterHost />
    </>
  );
}

/** Shown while the first route chunk loads. */
export function AppSplash() {
  const { t } = useTranslation('shell');
  return (
    <div className="flex min-h-dvh flex-col items-center justify-center gap-4" role="status">
      <ZoodWordmark size="lg" />
      <span className="sr-only">{t('loading')}</span>
    </div>
  );
}

/** Friendly page for route load errors (replaces React Router's developer error screen). */
export function RouteError() {
  const { t } = useTranslation(['errors', 'common']);
  return (
    <div className="flex min-h-dvh flex-col items-center justify-center gap-3 px-6 text-center">
      <ZoodWordmark size="md" />
      <h1 className="mt-4 text-xl font-semibold">{t('errors:generic.title')}</h1>
      <p className="text-muted">{t('errors:generic.description')}</p>
      <Button className="mt-2" onClick={() => window.location.reload()}>
        {t('common:actions.retry')}
      </Button>
    </div>
  );
}

export function NotFound() {
  const { t } = useTranslation('shell');
  return (
    <div className="flex min-h-dvh flex-col items-center justify-center gap-3 px-6 text-center">
      <ZoodWordmark size="md" />
      <h1 className="mt-4 text-xl font-semibold">{t('notFound.title')}</h1>
      <p className="text-muted">{t('notFound.description')}</p>
      <Button asChild className="mt-2">
        <Link to="/demo">{t('notFound.action')}</Link>
      </Button>
    </div>
  );
}
