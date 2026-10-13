import { Printer, Smartphone } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { PosDevice } from '@/domain/types';
import { Icon, List, ListRow, StatusChip } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatRelative } from '@/lib/format';
import { localize } from '@/lib/locale';
import { useDemoStore } from '@/mocks/store';

/** Operational status of POS devices (view-only; device administration belongs to ZOOD Operations). */
export function DeviceList({ devices, showStation = false }: { devices: PosDevice[]; showStation?: boolean }) {
  const { t } = useTranslation('station');
  const { locale } = useLocale();
  const stations = useDemoStore((state) => state.data.stations);
  const transactions = useDemoStore((state) => state.data.transactions);
  const lastSale = (deviceId: string) => {
    for (let index = transactions.length - 1; index >= 0; index--) {
      const txn = transactions[index];
      if (txn?.deviceId === deviceId && txn.status === 'completed') return txn.deviceAt;
    }
    return undefined;
  };

  return (
    <List>
      {devices.map((device) => (
        <ListRow
          key={device.id}
          chevron={false}
          leading={
            <span className="flex size-9 items-center justify-center rounded-full bg-surface-sunken text-muted">
              <Icon icon={Smartphone} size={18} />
            </span>
          }
          title={<span dir="ltr">{device.code}</span>}
          subtitle={
            <>
              {showStation && `${localize(stations[device.stationId]?.name, locale)} · `}
              {device.status === 'offline'
                ? t('devices.lastSeen', { time: formatRelative(device.lastSyncAt, locale) })
                : lastSale(device.id)
                  ? t('devices.lastActivity', { time: formatRelative(lastSale(device.id) ?? device.lastSyncAt, locale) })
                  : t('devices.lastSync', { time: formatRelative(device.lastSyncAt, locale) })}
            </>
          }
          trailing={
            <StatusChip
              tone={device.status === 'online' ? 'success' : device.status === 'offline' ? 'warning' : 'danger'}
              label={t(`devices.status.${device.status}`)}
            />
          }
          trailingSub={
            device.printer !== 'ready' && (
              <StatusChip tone={device.printer === 'error' ? 'danger' : 'warning'} icon={Printer} label={t(`devices.printer.${device.printer}`)} />
            )
          }
        />
      ))}
    </List>
  );
}
