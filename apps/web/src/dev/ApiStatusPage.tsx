import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { API_MODE } from '@/api';
import { API_BASE_URL, ApiRequestError } from '@/api/http-client';
import { platformApi, type ServiceMeta } from '@/api/platform';
import {
  Banner,
  BrandLogo,
  DateTimeText,
  ErrorState,
  LanguageSwitcher,
  Panel,
  Skeleton,
  StatusChip,
  ThemeSwitcher,
} from '@/design-system';
import { DetailList } from '@/shell/page';

type State = { status: 'loading' } | { status: 'ready'; meta: ServiceMeta } | { status: 'error'; error: unknown };

/** Technical values (addresses, versions) read left to right in both languages. */
function Technical({ children }: { children: string }) {
  return (
    <span dir="ltr" className="font-mono text-sm">
      {children}
    </span>
  );
}

/**
 * Developer page: shows what the web app receives from the ZAWED API, end to end. In mock mode it
 * says so instead of calling anything.
 */
export function ApiStatusPage() {
  const { t } = useTranslation('dev');
  const [state, setState] = useState<State>({ status: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const live = API_MODE === 'http';

  useEffect(() => {
    if (!live) return;
    const controller = new AbortController();
    platformApi.meta(controller.signal).then(
      (meta) => setState({ status: 'ready', meta }),
      (error: unknown) => {
        if (!controller.signal.aborted) setState({ status: 'error', error });
      },
    );
    return () => controller.abort();
  }, [live, attempt]);

  const retry = () => {
    setState({ status: 'loading' });
    setAttempt((value) => value + 1);
  };

  return (
    <div className="min-h-dvh bg-background">
      <header className="border-b border-border bg-surface">
        <div className="mx-auto flex max-w-3xl flex-wrap items-center gap-x-6 gap-y-3 px-4 py-3">
          <div className="flex items-center gap-3">
            <BrandLogo size="sm" variant="compact" />
            <div>
              <h1 className="text-base font-semibold">{t('api.title')}</h1>
              <p className="text-xs text-muted">{t('api.subtitle')}</p>
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2 sm:ms-auto">
            <LanguageSwitcher />
            <ThemeSwitcher />
          </div>
        </div>
      </header>

      <main className="mx-auto flex max-w-3xl flex-col gap-4 px-4 py-6">
        <Panel flush>
          <DetailList
            rows={[
              {
                label: t('api.source'),
                value: (
                  <StatusChip
                    tone={live ? 'brand' : 'neutral'}
                    label={live ? t('api.sourceHttp') : t('api.sourceMock')}
                  />
                ),
              },
              live && { label: t('api.address'), value: <Technical>{API_BASE_URL}</Technical> },
            ]}
          />
        </Panel>

        {!live && <Banner tone="info" title={t('api.mockTitle')} description={t('api.mockDescription')} />}

        {live && state.status === 'loading' && (
          <Panel>
            <div className="flex flex-col gap-3" role="status">
              <Skeleton className="h-5 w-2/3" />
              <Skeleton className="h-5 w-1/2" />
              <Skeleton className="h-5 w-3/5" />
            </div>
          </Panel>
        )}

        {live && state.status === 'error' && (
          <Panel>
            <ErrorState onRetry={retry} />
            <ErrorReference error={state.error} />
          </Panel>
        )}

        {live && state.status === 'ready' && (
          <Panel flush title={t('api.sourceHttp')} action={<StatusChip tone="success" label={t('api.connected')} />}>
            <DetailList
              rows={[
                { label: t('api.service'), value: <Technical>{state.meta.service}</Technical> },
                { label: t('api.version'), value: <Technical>{state.meta.version}</Technical> },
                { label: t('api.contractVersion'), value: <Technical>{String(state.meta.contractVersion)}</Technical> },
                { label: t('api.environment'), value: <Technical>{state.meta.environment}</Technical> },
                state.meta.schemaVersion !== undefined && {
                  label: t('api.schemaVersion'),
                  value: <Technical>{state.meta.schemaVersion}</Technical>,
                },
                { label: t('api.serverTime'), value: <DateTimeText value={state.meta.serverTime} /> },
              ]}
            />
          </Panel>
        )}
      </main>
    </div>
  );
}

/** The error code and the reference a person can quote to find the failure in the server log. */
function ErrorReference({ error }: { error: unknown }) {
  const { t } = useTranslation('dev');
  if (!(error instanceof ApiRequestError)) return null;
  return (
    <p className="pb-4 text-center text-sm text-muted">
      {error.requestId
        ? t('api.errorDetails', { code: error.code, requestId: error.requestId })
        : t('api.errorNoReference', { code: error.code })}
    </p>
  );
}
