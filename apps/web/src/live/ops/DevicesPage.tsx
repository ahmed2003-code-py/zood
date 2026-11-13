import { ChevronRight, Smartphone } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { DataView, Icon, ListRow, SegmentedControl, type DataColumn } from '@/design-system';
import { ListPanel, PageHeader } from '@/shell/page';
import { opsApi, type Device } from '../api';
import { DeviceStatusChip } from '../chips';
import { EmptyList, PagedList } from '../components';
import { DeviceActivity } from '../device-activity';
import { usePagedResource } from '../use-resource';

type StatusFilter = 'ALL' | Device['status'];

/** Ops: every POS device on the network; each row opens its station, where devices are managed. */
export function DevicesPage() {
  const { t } = useTranslation('live');
  const navigate = useNavigate();
  const [status, setStatus] = useState<StatusFilter>('ALL');
  const devices = usePagedResource(
    (page, signal) => opsApi.devices.list({ ...page, status: status === 'ALL' ? undefined : status }, signal),
    [status],
  );
  const open = (device: Device) => navigate(`/ops/stations/${device.stationId}`);

  const columns: DataColumn<Device>[] = [
    { id: 'code', header: t('devices.code'), cell: (device) => <span dir="ltr" className="font-mono font-medium">{device.code}</span> },
    { id: 'label', header: t('devices.label'), cell: (device) => device.label ?? t('devices.noLabel') },
    { id: 'activity', header: t('devices.activity'), cell: (device) => <DeviceActivity device={device} /> },
    { id: 'status', header: t('devices.status'), cell: (device) => <DeviceStatusChip status={device.status} /> },
    {
      id: 'station',
      header: t('devices.station'),
      cell: (device) => (
        <Link to={`/ops/stations/${device.stationId}`} className="inline-flex items-center gap-1 text-primary hover:underline">
          {t('devices.openStation')}
          <Icon icon={ChevronRight} size={14} directional />
        </Link>
      ),
    },
  ];

  return (
    <>
      <PageHeader title={t('devices.pageTitle')} description={t('devices.pageDescription')} />
      <div className="mb-4">
        <SegmentedControl<StatusFilter>
          label={t('devices.statusFilter')}
          value={status}
          onValueChange={setStatus}
          options={[
            { value: 'ALL', label: t('common.all') },
            { value: 'PENDING_ACTIVATION', label: t('enums.deviceStatus.PENDING_ACTIVATION') },
            { value: 'ACTIVE', label: t('enums.deviceStatus.ACTIVE') },
            { value: 'REVOKED', label: t('enums.deviceStatus.REVOKED') },
          ]}
        />
      </div>
      <ListPanel>
        <PagedList
          resource={devices}
          empty={
            <EmptyList
              title={status === 'ALL' ? t('devices.pageEmpty') : t('devices.noResults')}
              description={status === 'ALL' ? t('devices.pageEmptyHint') : t('devices.noResultsHint')}
            />
          }
        >
          {(items) => (
            <DataView
              caption={t('devices.pageTitle')}
              rows={items}
              columns={columns}
              getRowId={(device) => device.id}
              onRowClick={open}
              tableFrom="md"
              renderListItem={(device) => (
                <ListRow
                  leading={
                    <span className="flex size-9 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
                      <Icon icon={Smartphone} size={18} />
                    </span>
                  }
                  title={<span dir="ltr" className="font-mono">{device.code}</span>}
                  subtitle={device.label ?? <DeviceActivity device={device} />}
                  trailing={<DeviceStatusChip status={device.status} />}
                  onClick={() => open(device)}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
    </>
  );
}
