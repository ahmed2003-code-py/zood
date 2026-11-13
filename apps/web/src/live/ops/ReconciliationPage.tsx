import { CircleCheck, Play, TriangleAlert } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useSession } from '@/auth/session-store';
import { Button, DateTimeText, Icon, Money, StatusChip, toast } from '@/design-system';
import { ListPanel, PageHeader } from '@/shell/page';
import { opsApi, type ReconciliationRun } from '../api';
import { EmptyList, PagedList } from '../components';
import { liveError } from '../errors';
import { usePagedResource } from '../use-resource';

/**
 * Ops: the nightly comparison of every cached wallet balance with the ledger (PRD 4.2). The ledger
 * is the truth; a finding means the cached balance must be investigated, never that money moved.
 */
export function ReconciliationPage() {
  const { t } = useTranslation('live');
  const role = useSession((state) => state.user?.role);
  const [running, setRunning] = useState(false);
  const [open, setOpen] = useState<string>();
  const runs = usePagedResource((page, signal) => opsApi.reconciliation.runs(page, signal), [], 10);

  const runNow = async () => {
    setRunning(true);
    try {
      await opsApi.reconciliation.runNow();
      toast(t('reconciliation.running'));
      runs.reload();
    } catch (error) {
      toast(liveError(t, error).message, 'danger');
    } finally {
      setRunning(false);
    }
  };

  return (
    <>
      <PageHeader
        title={t('reconciliation.title')}
        description={t('reconciliation.description')}
        actions={
          role === 'SUPER_ADMIN' ? (
            <Button leadingIcon={Play} loading={running} onClick={runNow}>
              {t('reconciliation.runNow')}
            </Button>
          ) : undefined
        }
      />
      {role !== 'SUPER_ADMIN' && <p className="-mt-3 mb-4 text-sm text-muted">{t('reconciliation.superAdminOnly')}</p>}
      <ListPanel>
        <PagedList resource={runs} empty={<EmptyList title={t('reconciliation.empty')} description={t('reconciliation.emptyHint')} />}>
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((run) => (
                <RunRow key={run.id} run={run} open={open === run.id} onToggle={() => setOpen(open === run.id ? undefined : run.id)} />
              ))}
            </div>
          )}
        </PagedList>
      </ListPanel>
    </>
  );
}

function RunRow({ run, open, onToggle }: { run: ReconciliationRun; open: boolean; onToggle: () => void }) {
  const { t } = useTranslation('live');
  const clean = run.findingsCount === 0;
  return (
    <div className="flex flex-col">
      <button type="button" onClick={onToggle} disabled={clean} className="flex items-center gap-3 px-4 py-3 text-start enabled:hover:bg-surface-sunken">
        <Icon icon={clean ? CircleCheck : TriangleAlert} className={clean ? 'text-success' : 'text-danger'} />
        <div className="min-w-0 flex-1">
          <p className="font-medium">
            <DateTimeText value={run.startedAt} /> · {t(`reconciliation.trigger.${run.trigger}`)}
          </p>
          <p className="text-xs text-muted">
            {run.finishedAt ? t('reconciliation.walletsChecked', { count: run.walletsChecked }) : t('reconciliation.unfinished')}
          </p>
        </div>
        <StatusChip
          tone={clean ? 'success' : 'danger'}
          label={clean ? t('reconciliation.clean') : t('reconciliation.findings', { count: run.findingsCount })}
        />
      </button>
      {open && <Findings runId={run.id} />}
    </div>
  );
}

function Findings({ runId }: { runId: string }) {
  const { t } = useTranslation('live');
  const findings = usePagedResource((page, signal) => opsApi.reconciliation.findings(runId, page, signal), [runId]);
  return (
    <div className="border-t border-border bg-surface-sunken">
      <PagedList resource={findings} empty={null}>
        {(items) => (
          <ul className="divide-y divide-border">
            {items.map((finding) => (
              <li key={finding.id} className="flex flex-col gap-1 px-4 py-3 text-sm sm:flex-row sm:items-center sm:justify-between">
                <div className="min-w-0">
                  <p className="font-medium">{t(`reconciliation.kind.${finding.kind}`)}</p>
                  <Link to={`/ops/companies/${finding.companyId}`} dir="ltr" className="font-mono text-xs text-muted hover:underline">
                    {finding.companyId}
                  </Link>
                </div>
                {finding.kind === 'BALANCE_MISMATCH' && (
                  <dl className="grid grid-cols-3 gap-3 text-end tabular-nums">
                    <div>
                      <dt className="text-xs text-muted">{t('reconciliation.cached')}</dt>
                      <dd>{finding.cachedBalancePiastres != null && <Money value={finding.cachedBalancePiastres} />}</dd>
                    </div>
                    <div>
                      <dt className="text-xs text-muted">{t('reconciliation.ledger')}</dt>
                      <dd>{finding.ledgerBalancePiastres != null && <Money value={finding.ledgerBalancePiastres} />}</dd>
                    </div>
                    <div>
                      <dt className="text-xs text-muted">{t('reconciliation.difference')}</dt>
                      <dd className="font-semibold text-danger">
                        {finding.differencePiastres != null && <Money value={finding.differencePiastres} signDisplay="always" />}
                      </dd>
                    </div>
                  </dl>
                )}
              </li>
            ))}
          </ul>
        )}
      </PagedList>
    </div>
  );
}
