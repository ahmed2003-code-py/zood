import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CloudOff, Wifi } from 'lucide-react';
import { DataView, DateTimeText, ListRow, SegmentedControl, StatusChip, type DataColumn } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { ListPanel, PageHeader } from '@/shell/page';
import { opsApi, type DeviceSync } from '../api';
import { EmptyList, PagedList } from '../components';
import { usePagedResource } from '../use-resource';

type Filter = 'ALL' | 'STALE';

/** What the last batch did, in one line: recorded, already received, refused, still waiting. */
function LastBatch({ device }: { device: DeviceSync }) {
  const { t } = useTranslation('live');
  if (!device.lastBatch) return <span className="text-muted">{t('sync.noBatch')}</span>;
  const batch = device.lastBatch;
  return (
    <span className="flex flex-col gap-0.5">
      <DateTimeText value={batch.receivedAt} />
      <span className="text-xs text-muted">
        {t('sync.batchCounts', { recorded: batch.recorded, duplicates: batch.duplicates, rejected: batch.rejected })}
      </span>
    </span>
  );
}

function SyncState({ device }: { device: DeviceSync }) {
  const { t } = useTranslation('live');
  return device.stale ? (
    <StatusChip tone="warning" icon={CloudOff} label={t('sync.stale')} />
  ) : (
    <StatusChip tone="success" icon={Wifi} label={t('sync.recent')} />
  );
}

/**
 * The sync monitor (B5 contract §5): every active terminal, when it last reached the server, what
 * its last batch did and what it still holds. A terminal silent for a day is flagged.
 */
export function SyncPage() {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const [filter, setFilter] = useState<Filter>('ALL');
  const devices = usePagedResource((page, signal) => opsApi.sync.devices({ ...page, stale: filter === 'STALE' || undefined }, signal), [filter]);
  const station = (device: DeviceSync) => (locale === 'ar' ? device.stationNameAr : device.stationNameEn);

  const columns: DataColumn<DeviceSync>[] = [
    { id: 'device', header: t('sync.device'), cell: (device) => <span dir="ltr">{device.code}</span> },
    { id: 'station', header: t('sync.station'), cell: station },
    {
      id: 'lastSync',
      header: t('sync.lastSync'),
      cell: (device) => (device.lastSyncAt ? <DateTimeText value={device.lastSyncAt} /> : <span className="text-muted">{t('sync.never')}</span>),
    },
    { id: 'batch', header: t('sync.lastBatch'), cell: (device) => <LastBatch device={device} /> },
    { id: 'pending', header: t('sync.pending'), align: 'end', cell: (device) => <span className="tabular-nums">{device.lastBatch?.pending ?? 0}</span> },
    { id: 'state', header: t('sync.state'), cell: (device) => <SyncState device={device} /> },
  ];

  return (
    <>
      <PageHeader title={t('sync.title')} description={t('sync.description')} />
      <div className="mb-4">
        <SegmentedControl<Filter>
          label={t('sync.filter')}
          value={filter}
          onValueChange={setFilter}
          options={[
            { value: 'ALL', label: t('common.all') },
            { value: 'STALE', label: t('sync.stale') },
          ]}
        />
      </div>
      <ListPanel>
        <PagedList resource={devices} empty={<EmptyList title={t(filter === 'STALE' ? 'sync.noneStale' : 'sync.empty')} description={t('sync.emptyHint')} />}>
          {(items) => (
            <DataView
              caption={t('sync.title')}
              rows={items}
              columns={columns}
              getRowId={(device) => device.deviceId}
              tableFrom="lg"
              renderListItem={(device) => (
                <ListRow
                  title={<span dir="ltr">{device.code}</span>}
                  subtitle={
                    <>
                      {station(device)}
                      {' · '}
                      {device.lastSyncAt ? <DateTimeText value={device.lastSyncAt} /> : t('sync.never')}
                    </>
                  }
                  trailing={<SyncState device={device} />}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
    </>
  );
}
