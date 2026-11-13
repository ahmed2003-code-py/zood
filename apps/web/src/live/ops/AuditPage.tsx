import { ChevronDown } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiRequestError } from '@/api/http-client';
import { DateTimeText, Icon } from '@/design-system';
import { ListPanel, PageHeader } from '@/shell/page';
import { adminApi, type AuditEntry } from '../api';
import { EmptyList, PagedList } from '../components';
import { usePagedResource } from '../use-resource';

/** The translated label for a code, or the raw code when there is none. */
function Label({ group, code }: { group: 'actions' | 'entities'; code: string }) {
  const { t } = useTranslation('live');
  return <>{t(`audit.${group}.${code}`, { defaultValue: code })}</>;
}

function Entry({ entry }: { entry: AuditEntry }) {
  const { t } = useTranslation('live');
  const [open, setOpen] = useState(false);
  const hasMetadata = Object.keys(entry.metadata).length > 0;
  return (
    <div className="px-4 py-3">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="font-medium">
            <Label group="actions" code={entry.action} />
          </p>
          <p className="mt-0.5 text-xs text-muted">
            <DateTimeText value={entry.occurredAt} />
          </p>
        </div>
        {entry.actorRole && (
          <span className="shrink-0 text-sm text-muted">{t(`enums.userRole.${entry.actorRole}`, { defaultValue: entry.actorRole })}</span>
        )}
      </div>
      <dl className="mt-2 grid grid-cols-1 gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
        <div className="flex gap-2">
          <dt className="text-muted">{t('audit.entity')}</dt>
          <dd className="min-w-0">
            <Label group="entities" code={entry.entityType} />
            {entry.entityId && (
              <span dir="ltr" className="ms-2 break-all font-mono text-xs text-muted">
                {entry.entityId}
              </span>
            )}
          </dd>
        </div>
        <div className="flex gap-2">
          <dt className="text-muted">{t('audit.scope')}</dt>
          <dd className="min-w-0">
            {t(`users.scope.${entry.scopeType}`, { defaultValue: entry.scopeType })}
            {entry.scopeId && (
              <span dir="ltr" className="ms-2 break-all font-mono text-xs text-muted">
                {entry.scopeId}
              </span>
            )}
          </dd>
        </div>
        {entry.ipPrefix && (
          <div className="flex gap-2">
            <dt className="text-muted">{t('audit.ip')}</dt>
            <dd dir="ltr" className="font-mono text-xs">
              {entry.ipPrefix}
            </dd>
          </div>
        )}
      </dl>
      {hasMetadata && (
        <div className="mt-2">
          <button
            type="button"
            aria-expanded={open}
            onClick={() => setOpen((value) => !value)}
            className="inline-flex min-h-9 items-center gap-1.5 rounded-control text-sm font-medium text-primary"
          >
            <Icon icon={ChevronDown} size={16} className={open ? 'rotate-180' : undefined} />
            {t(open ? 'audit.hideDetails' : 'audit.showDetails')}
          </button>
          {open && (
            <pre dir="ltr" className="mt-1 overflow-x-auto rounded-control bg-surface-sunken p-3 text-start font-mono text-xs">
              {JSON.stringify(entry.metadata, null, 2)}
            </pre>
          )}
        </div>
      )}
    </div>
  );
}

/** Super Admin: the audit log, newest first. Ops gets a 403 from the API and an explanation here. */
export function AuditPage() {
  const { t } = useTranslation('live');
  const audit = usePagedResource((page, signal) => adminApi.audit.list(page, signal), []);
  const forbidden = audit.error instanceof ApiRequestError && audit.error.status === 403;

  return (
    <>
      <PageHeader title={t('audit.title')} description={t('audit.description')} />
      <ListPanel>
        {forbidden ? (
          <EmptyList title={t('audit.forbidden')} description={t('audit.forbiddenHint')} />
        ) : (
          <PagedList resource={audit} empty={<EmptyList title={t('audit.empty')} description={t('audit.emptyHint')} />}>
            {(items) => (
              <div className="divide-y divide-border">
                {items.map((entry) => (
                  <Entry key={entry.id} entry={entry} />
                ))}
              </div>
            )}
          </PagedList>
        )}
      </ListPanel>
    </>
  );
}
