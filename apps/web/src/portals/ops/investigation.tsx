/** Operations: alert investigation (timeline + demo actions) and transaction details. */
import { CircleCheck, ShieldOff, TriangleAlert } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';
import { Button, DateTimeText, EmptyState, Icon, Plate, StatusChip, toast } from '@/design-system';
import { TransactionDetail } from '@/features/transactions/TransactionDetail';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatVolume } from '@/lib/format';
import { useDemoStore } from '@/mocks/store';
import { BackLink, ListPanel, PageHeader, PageSection } from '@/shell/page';

const SECOND = 1000;

export function OpsTransactionDetail() {
  const { t } = useTranslation('transactions');
  const { id } = useParams();
  const transaction = useDemoStore((state) => state.data.transactions.find((txn) => txn.id === id));
  return (
    <>
      <BackLink to="/ops/transactions" label={t('detail.back')} />
      <PageHeader title={t('detail.title')} />
      {transaction ? <TransactionDetail transaction={transaction} showCompany /> : <EmptyState title={t('detail.notFound')} description="" />}
    </>
  );
}

/**
 * Alert investigation. For a tank-capacity anomaly the fueling stays valid (non-blocking, PRD §10);
 * Operations reviews the timeline and may suspend the tag (demo action, no approval workflow).
 */
export function OpsAlertDetail() {
  const { t } = useTranslation(['ops', 'alerts', 'status']);
  const { locale } = useLocale();
  const { id } = useParams();
  const data = useDemoStore((state) => state.data);
  const setTagStatus = useDemoStore((state) => state.setTagStatus);
  const resolveAlert = useDemoStore((state) => state.resolveAlert);
  const alert = data.alerts.find((item) => item.id === id);

  if (!alert) {
    return (
      <>
        <BackLink to="/ops/alerts" label={t('alertDetail.back')} />
        <EmptyState title={t('alertDetail.notFound')} description="" />
      </>
    );
  }

  const txn = alert.transactionId ? data.transactions.find((item) => item.id === alert.transactionId) : undefined;
  const vehicle = alert.vehicleId ? data.vehicles[alert.vehicleId] : undefined;
  const tag = vehicle ? data.tags[vehicle.tagId] : undefined;
  const tagActive = tag?.status === 'active';

  // Timeline reconstructed from the transaction record (production: the transaction event log).
  const timeline = txn
    ? [
        { key: 'scanned', at: new Date(new Date(txn.deviceAt).getTime() - 40 * SECOND) },
        { key: 'resolved', at: new Date(new Date(txn.deviceAt).getTime() - 39 * SECOND) },
        { key: 'authorized', at: new Date(new Date(txn.deviceAt).getTime() - 38 * SECOND) },
        { key: 'entered', at: new Date(new Date(txn.deviceAt).getTime() - 6 * SECOND) },
        { key: 'completed', at: new Date(txn.receivedAt ?? txn.deviceAt) },
        ...(alert.type === 'tankCapacityExceeded' ? [{ key: 'detected', at: new Date(alert.at), warn: true }] : []),
        { key: 'created', at: new Date(alert.at), warn: true },
      ]
    : [];

  return (
    <>
      <BackLink to="/ops/alerts" label={t('alertDetail.back')} />
      <PageHeader title={t(`alerts:types.${alert.type}`)} description={t('alertDetail.title')} />
      <div className="grid grid-cols-1 gap-8 lg:grid-cols-5">
        <div className="flex min-w-0 flex-col gap-6 lg:col-span-2">
          <div className="flex flex-col gap-3 rounded-container border border-border bg-surface p-4">
            <div className="flex flex-wrap items-center gap-2">
              <StatusChip
                tone={alert.severity === 'critical' ? 'danger' : alert.severity === 'warning' ? 'warning' : 'info'}
                label={t(`alerts:severity.${alert.severity}`)}
              />
              <StatusChip tone={alert.status === 'open' ? 'warning' : 'success'} label={t(alert.status === 'open' ? 'alertDetail.open' : 'alertDetail.resolved')} />
            </div>
            {vehicle && <Plate value={vehicle.plate} size="md" className="self-start" />}
            {txn && vehicle && alert.type === 'tankCapacityExceeded' && (
              <p className="flex items-center gap-2 rounded-control bg-danger-subtle px-3 py-2 text-sm font-semibold text-danger">
                <Icon icon={TriangleAlert} size={18} />
                {t('alertDetail.tankCheck', {
                  dispensed: formatVolume(txn.volumeMl, locale, 0),
                  tank: formatVolume(vehicle.tankCapacityMl, locale, 0),
                })}
              </p>
            )}
            {txn && <p className="text-sm text-muted">{t('alertDetail.nonBlocking')}</p>}
          </div>

          <PageSection title={t('alertDetail.actions')}>
            <div className="flex flex-col gap-2 sm:flex-row lg:flex-col">
              {tag && (
                <Button
                  variant="danger"
                  leadingIcon={ShieldOff}
                  disabled={!tagActive}
                  onClick={() => {
                    setTagStatus(tag.id, 'deactivated');
                    toast(t('alertDetail.tagSuspended'), 'warning');
                  }}
                >
                  {tagActive ? t('alertDetail.suspendTag') : t('status:tag.deactivated')}
                </Button>
              )}
              <Button variant="secondary" leadingIcon={CircleCheck} disabled={alert.status !== 'open'} onClick={() => resolveAlert(alert.id)}>
                {alert.status === 'open' ? t('alertDetail.resolve') : t('alertDetail.resolved')}
              </Button>
            </div>
          </PageSection>

          {timeline.length > 0 && (
            <PageSection title={t('alertDetail.timeline')}>
              <ListPanel>
                <ol className="flex flex-col px-4 py-3">
                  {timeline.map((step, index) => (
                    <li key={step.key} className="relative flex gap-3 pb-4 last:pb-0">
                      {index < timeline.length - 1 && <span aria-hidden className="absolute start-[0.6875rem] top-6 bottom-0 w-px bg-border" />}
                      <span
                        className={cn(
                          'relative mt-0.5 flex size-6 shrink-0 items-center justify-center rounded-full',
                          'warn' in step && step.warn ? 'bg-danger-subtle text-danger' : 'bg-success-subtle text-success',
                        )}
                      >
                        <Icon icon={'warn' in step && step.warn ? TriangleAlert : CircleCheck} size={14} />
                      </span>
                      <div className="min-w-0">
                        <p className="text-sm font-medium">
                          {step.key === 'entered'
                            ? t('alertDetail.steps.entered', { volume: formatVolume(txn?.volumeMl ?? 0, locale) })
                            : t(`alertDetail.steps.${step.key as 'scanned'}`)}
                        </p>
                        <p className="text-xs text-muted">
                          <DateTimeText value={step.at.toISOString()} variant="time" />
                        </p>
                      </div>
                    </li>
                  ))}
                </ol>
              </ListPanel>
            </PageSection>
          )}
        </div>
        {txn && (
          <PageSection title={t('alertDetail.linked')} className="lg:col-span-3">
            <TransactionDetail transaction={txn} showCompany />
          </PageSection>
        )}
      </div>
    </>
  );
}
