import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, SegmentedControl, toast } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { ListPanel, PageHeader } from '@/shell/page';
import { opsApi, type Alert, type AlertStatus, type AlertType } from '../api';
import { EmptyList, PagedList } from '../components';
import { liveError } from '../errors';
import { AlertRow } from '../fuelings';
import { usePagedResource, useResource } from '../use-resource';

type StatusFilter = 'ALL' | AlertStatus;
type TypeFilter = 'ALL' | AlertType;

/**
 * Ops: the hard alerts the fueling engine raised. An open one is acknowledged once someone has
 * looked at it; the API records who, and refuses anyone without the alerts permission.
 */
export function AlertsPage() {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const [status, setStatus] = useState<StatusFilter>('OPEN');
  const [type, setType] = useState<TypeFilter>('ALL');
  const [busy, setBusy] = useState<string>();
  const alerts = usePagedResource(
    (page, signal) =>
      opsApi.alerts.list({ ...page, status: status === 'ALL' ? undefined : status, type: type === 'ALL' ? undefined : type }, signal),
    [status, type],
  );
  // Names for the companies on screen: the first 100 cover a pilot; beyond that the alert shows no name.
  const companies = useResource((signal) => opsApi.companies.list({ limit: 100 }, signal), []);
  const companyName = (id?: string) => {
    const company = id ? companies.data?.items.find((item) => item.id === id) : undefined;
    return company ? (locale === 'ar' ? company.nameAr : company.nameEn) : undefined;
  };

  const acknowledge = async (alert: Alert) => {
    setBusy(alert.id);
    try {
      await opsApi.alerts.acknowledge(alert.id);
      toast(t('alerts.acknowledged'));
      alerts.reload();
    } catch (error) {
      toast(liveError(t, error).message, 'danger');
    } finally {
      setBusy(undefined);
    }
  };

  return (
    <>
      <PageHeader title={t('alerts.title')} description={t('alerts.opsDescription')} />
      <div className="mb-4 flex flex-col gap-3 md:flex-row md:flex-wrap md:items-end">
        <SegmentedControl<StatusFilter>
          label={t('alerts.statusFilter')}
          value={status}
          onValueChange={setStatus}
          options={[
            { value: 'OPEN', label: t('alerts.status.OPEN') },
            { value: 'ACKNOWLEDGED', label: t('alerts.status.ACKNOWLEDGED') },
            { value: 'ALL', label: t('common.all') },
          ]}
        />
        <SegmentedControl<TypeFilter>
          label={t('alerts.typeFilter')}
          value={type}
          onValueChange={setType}
          options={[
            { value: 'ALL', label: t('common.all') },
            { value: 'TANK_CAPACITY_EXCEEDED', label: t('alerts.type.TANK_CAPACITY_EXCEEDED') },
            { value: 'VIP_CEILING_PASSED', label: t('alerts.type.VIP_CEILING_PASSED') },
            { value: 'LOW_BALANCE', label: t('alerts.type.LOW_BALANCE') },
          ]}
        />
      </div>
      <ListPanel>
        <PagedList resource={alerts} empty={<EmptyList title={t('alerts.empty')} description={t('alerts.emptyHint')} />}>
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((alert) => (
                <AlertRow
                  key={alert.id}
                  alert={alert}
                  fuelingPath="/ops/transactions"
                  context={
                    alert.companyId && (
                      <Link to={`/ops/companies/${alert.companyId}`} className="hover:underline">
                        {companyName(alert.companyId) ?? t('alerts.company')}
                      </Link>
                    )
                  }
                  actions={
                    alert.status === 'OPEN' && (
                      <Button size="sm" loading={busy === alert.id} disabled={busy !== undefined} onClick={() => acknowledge(alert)}>
                        {t('alerts.acknowledge')}
                      </Button>
                    )
                  }
                />
              ))}
            </div>
          )}
        </PagedList>
      </ListPanel>
    </>
  );
}
