import { Ban, Pause, Pencil, Sun } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { Banner, Button, DateTimeText, ErrorState, Skeleton, toast } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { BackLink, DetailList, ListPanel, PageHeader, PageSection } from '@/shell/page';
import { opsApi, type StationStatus } from '../api';
import { StationStatusChip } from '../chips';
import { ReasonSheet } from '../components';
import { useResource } from '../use-resource';
import { AgentsSection } from './station-agents';
import { DevicesSection } from './station-devices';
import { StationFormSheet } from './station-form';

type Dialog = { kind: 'edit' } | { kind: 'status'; to: StationStatus } | undefined;

/** Ops: one station with its devices and agents. */
export function StationPage() {
  const { id = '' } = useParams();
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const [dialog, setDialog] = useState<Dialog>();
  const station = useResource((signal) => opsApi.stations.get(id, signal), [id]);
  const operatorId = station.data?.operatorId;
  const operator = useResource(
    (signal) => (operatorId ? opsApi.operators.get(operatorId, signal) : Promise.resolve(undefined)),
    [operatorId],
  );
  const close = () => setDialog(undefined);

  if (station.error && !station.data) {
    return (
      <>
        <BackLink to="/ops/stations" label={t('station.back')} />
        <ErrorState onRetry={station.reload} />
      </>
    );
  }
  if (!station.data) {
    return (
      <div className="flex flex-col gap-4" aria-busy>
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  const data = station.data;
  const name = locale === 'ar' ? data.nameAr : data.nameEn;
  const closed = data.status === 'CLOSED';
  const suspended = data.status === 'SUSPENDED';
  const operatorName = operator.data ? (locale === 'ar' ? operator.data.nameAr : operator.data.nameEn) : t('station.viewOperator');

  return (
    <>
      <BackLink to="/ops/stations" label={t('station.back')} />
      <PageHeader
        title={name}
        description={data.address}
        actions={
          !closed && (
            <>
              <Button variant="secondary" leadingIcon={Pencil} onClick={() => setDialog({ kind: 'edit' })}>
                {t('station.edit')}
              </Button>
              {suspended ? (
                <Button leadingIcon={Sun} onClick={() => setDialog({ kind: 'status', to: 'ACTIVE' })}>
                  {t('station.reactivate')}
                </Button>
              ) : (
                <Button variant="secondary" leadingIcon={Pause} onClick={() => setDialog({ kind: 'status', to: 'SUSPENDED' })}>
                  {t('station.suspend')}
                </Button>
              )}
              <Button variant="ghost" leadingIcon={Ban} onClick={() => setDialog({ kind: 'status', to: 'CLOSED' })}>
                {t('station.close')}
              </Button>
            </>
          )
        }
      />
      {closed && <Banner tone="info" className="mb-6" title={t('station.closedBanner')} description={t('station.closedBannerHint')} />}
      {suspended && <Banner tone="warning" className="mb-6" title={t('station.suspendedBanner')} description={t('station.suspendedBannerHint')} />}

      <div className="grid grid-cols-1 gap-8 lg:grid-cols-5">
        <PageSection className="lg:col-span-2" title={t('station.details')}>
          <ListPanel>
            <DetailList
              rows={[
                { label: t('stations.columns.status'), value: <StationStatusChip status={data.status} /> },
                {
                  label: t('station.operator'),
                  value: (
                    <Link to={`/ops/operators/${data.operatorId}`} className="text-primary hover:underline">
                      {operatorName}
                    </Link>
                  ),
                },
                { label: t('stationForm.nameAr'), value: <span lang="ar">{data.nameAr}</span> },
                { label: t('stationForm.nameEn'), value: <span lang="en">{data.nameEn}</span> },
                { label: t('stationForm.address'), value: data.address ?? t('common.notSet') },
                {
                  label: t('station.coordinates'),
                  value:
                    // The API sends null for a station without a location, not an absent field.
                    data.latitude != null && data.longitude != null ? (
                      <span dir="ltr">{`${data.latitude}, ${data.longitude}`}</span>
                    ) : (
                      t('common.notSet')
                    ),
                },
                { label: t('stations.columns.timeZone'), value: <span dir="ltr">{data.timeZone}</span> },
                { label: t('stations.columns.connectivity'), value: t(`enums.connectivity.${data.connectivity}`) },
                { label: t('stations.columns.fuels'), value: data.fuelTypes.map((fuel) => t(`enums.fuel.${fuel}`)).join(t('common.separator')) },
                { label: t('common.createdAt'), value: <DateTimeText value={data.createdAt} variant="date" /> },
              ]}
            />
          </ListPanel>
        </PageSection>

        <div className="flex min-w-0 flex-col gap-8 lg:col-span-3">
          <DevicesSection stationId={id} closed={closed} />
          <AgentsSection stationId={id} closed={closed} />
        </div>
      </div>

      {dialog?.kind === 'edit' && <StationFormSheet open onOpenChange={close} station={data} onSaved={station.reload} />}
      <ReasonSheet
        open={dialog?.kind === 'status'}
        onOpenChange={close}
        title={
          dialog?.kind === 'status'
            ? t(dialog.to === 'SUSPENDED' ? 'station.suspendTitle' : dialog.to === 'ACTIVE' ? 'station.reactivateTitle' : 'station.closeTitle', { name })
            : ''
        }
        description={
          dialog?.kind === 'status'
            ? t(dialog.to === 'SUSPENDED' ? 'station.suspendHint' : dialog.to === 'ACTIVE' ? 'station.reactivateHint' : 'station.closeHint')
            : undefined
        }
        submitLabel={
          dialog?.kind === 'status'
            ? t(dialog.to === 'SUSPENDED' ? 'station.suspend' : dialog.to === 'ACTIVE' ? 'station.reactivate' : 'station.close')
            : ''
        }
        submitVariant={dialog?.kind === 'status' && dialog.to !== 'ACTIVE' ? 'danger' : 'primary'}
        onConfirm={async (reason) => {
          if (dialog?.kind !== 'status') return;
          await opsApi.stations.setStatus(id, { status: dialog.to, reason });
          toast(t('station.statusChanged'));
          station.reload();
        }}
      />
    </>
  );
}
