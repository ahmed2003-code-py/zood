import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { SegmentedControl } from '@/design-system';
import { ListPanel, PageHeader, ViewOnlyNote } from '@/shell/page';
import { companyApi, type AlertStatus } from '../api';
import { EmptyList, PagedList } from '../components';
import { AlertRow } from '../fuelings';
import { usePagedResource } from '../use-resource';

type StatusFilter = 'ALL' | AlertStatus;

/** Company portal: alerts about the company's own fueling and wallet, read only. */
export function CompanyAlertsPage() {
  const { t } = useTranslation('live');
  const [status, setStatus] = useState<StatusFilter>('ALL');
  const alerts = usePagedResource(
    (page, signal) => companyApi.alerts({ ...page, status: status === 'ALL' ? undefined : status }, signal),
    [status],
  );

  return (
    <>
      <PageHeader title={t('alerts.title')} description={t('alerts.companyDescription')} />
      <SegmentedControl<StatusFilter>
        className="mb-4"
        label={t('alerts.statusFilter')}
        value={status}
        onValueChange={setStatus}
        options={[
          { value: 'ALL', label: t('common.all') },
          { value: 'OPEN', label: t('alerts.status.OPEN') },
          { value: 'ACKNOWLEDGED', label: t('alerts.status.ACKNOWLEDGED') },
        ]}
      />
      <ListPanel>
        <PagedList resource={alerts} empty={<EmptyList title={t('alerts.empty')} description={t('alerts.emptyHint')} />}>
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((alert) => (
                <AlertRow key={alert.id} alert={alert} fuelingPath="/company/transactions" />
              ))}
            </div>
          )}
        </PagedList>
      </ListPanel>
      <div className="mt-4">
        <ViewOnlyNote />
      </div>
    </>
  );
}
