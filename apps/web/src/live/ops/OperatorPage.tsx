import { Pause, Pencil, Plus, Sun } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';
import { Banner, Button, DateTimeText, ErrorState, ListRow, Skeleton, toast } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { BackLink, DetailList, ListPanel, PageHeader, PageSection } from '@/shell/page';
import { opsApi } from '../api';
import { OperatorStatusChip, StationStatusChip } from '../chips';
import { EmptyList, PagedList, ReasonSheet } from '../components';
import { usePagedResource, useResource } from '../use-resource';
import { OperatorFormSheet } from './operator-form';
import { StationFormSheet } from './station-form';

type Dialog = 'edit' | 'status' | 'station' | undefined;

/** Ops: one station operator and its stations. */
export function OperatorPage() {
  const { id = '' } = useParams();
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const navigate = useNavigate();
  const [dialog, setDialog] = useState<Dialog>();
  const operator = useResource((signal) => opsApi.operators.get(id, signal), [id]);
  const stations = usePagedResource((page, signal) => opsApi.stations.list({ ...page, operatorId: id }, signal), [id]);
  const close = () => setDialog(undefined);

  if (operator.error && !operator.data) {
    return (
      <>
        <BackLink to="/ops/stations" label={t('operator.back')} />
        <ErrorState onRetry={operator.reload} />
      </>
    );
  }
  if (!operator.data) {
    return (
      <div className="flex flex-col gap-4" aria-busy>
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  const data = operator.data;
  const name = locale === 'ar' ? data.nameAr : data.nameEn;
  const suspended = data.status === 'SUSPENDED';

  return (
    <>
      <BackLink to="/ops/stations" label={t('operator.back')} />
      <PageHeader
        title={name}
        description={data.legalName}
        actions={
          <>
            <Button variant="secondary" leadingIcon={Pencil} onClick={() => setDialog('edit')}>
              {t('operator.edit')}
            </Button>
            <Button variant={suspended ? 'primary' : 'secondary'} leadingIcon={suspended ? Sun : Pause} onClick={() => setDialog('status')}>
              {t(suspended ? 'operator.reactivate' : 'operator.suspend')}
            </Button>
          </>
        }
      />
      {suspended && <Banner tone="warning" className="mb-6" title={t('operator.suspendedBanner')} description={t('operator.suspendedBannerHint')} />}

      <div className="grid grid-cols-1 gap-8 lg:grid-cols-5">
        <PageSection className="lg:col-span-2" title={t('operator.details')}>
          <ListPanel>
            <DetailList
              rows={[
                { label: t('operators.columns.status'), value: <OperatorStatusChip status={data.status} /> },
                { label: t('operatorForm.taxId'), value: <span dir="ltr">{data.taxId ?? t('common.notSet')}</span> },
                {
                  label: t('operatorForm.commercialRegistration'),
                  value: <span dir="ltr">{data.commercialRegistration ?? t('common.notSet')}</span>,
                },
                { label: t('operatorForm.contactName'), value: data.contactName ?? t('common.notSet') },
                { label: t('operatorForm.contactPhone'), value: <span dir="ltr">{data.contactPhone ?? t('common.notSet')}</span> },
                { label: t('operatorForm.contactEmail'), value: <span dir="ltr">{data.contactEmail ?? t('common.notSet')}</span> },
                { label: t('common.createdAt'), value: <DateTimeText value={data.createdAt} variant="date" /> },
              ]}
            />
          </ListPanel>
        </PageSection>

        <PageSection
          className="lg:col-span-3"
          title={t('operator.stations')}
          action={
            <Button variant="ghost" size="sm" leadingIcon={Plus} onClick={() => setDialog('station')}>
              {t('operator.addStation')}
            </Button>
          }
        >
          <ListPanel>
            <PagedList
              resource={stations}
              empty={
                <EmptyList
                  title={t('operator.noStations')}
                  description={t('operator.noStationsHint')}
                  action={
                    <Button leadingIcon={Plus} onClick={() => setDialog('station')}>
                      {t('operator.addStation')}
                    </Button>
                  }
                />
              }
            >
              {(items) => (
                <div className="divide-y divide-border">
                  {items.map((station) => (
                    <ListRow
                      key={station.id}
                      title={locale === 'ar' ? station.nameAr : station.nameEn}
                      subtitle={station.fuelTypes.map((fuel) => t(`enums.fuel.${fuel}`)).join(t('common.separator'))}
                      trailing={<StationStatusChip status={station.status} />}
                      onClick={() => navigate(`/ops/stations/${station.id}`)}
                    />
                  ))}
                </div>
              )}
            </PagedList>
          </ListPanel>
        </PageSection>
      </div>

      {dialog === 'edit' && <OperatorFormSheet open onOpenChange={close} operator={data} onSaved={operator.reload} />}
      <ReasonSheet
        open={dialog === 'status'}
        onOpenChange={close}
        title={t(suspended ? 'operator.reactivateTitle' : 'operator.suspendTitle', { name })}
        description={t(suspended ? 'operator.reactivateHint' : 'operator.suspendHint')}
        submitLabel={t(suspended ? 'operator.reactivate' : 'operator.suspend')}
        submitVariant={suspended ? 'primary' : 'danger'}
        onConfirm={async (reason) => {
          await opsApi.operators.setStatus(id, { status: suspended ? 'ACTIVE' : 'SUSPENDED', reason });
          toast(t(suspended ? 'operator.reactivated' : 'operator.suspended'));
          operator.reload();
        }}
      />
      {dialog === 'station' && (
        <StationFormSheet open onOpenChange={close} operatorId={id} onSaved={(station) => navigate(`/ops/stations/${station.id}`)} />
      )}
    </>
  );
}
