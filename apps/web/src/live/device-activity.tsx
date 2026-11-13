import { useTranslation } from 'react-i18next';
import { DateTimeText } from '@/design-system';
import type { Device } from './api';

/** When a device must be activated by (pending), or when it last synced (active). */
export function DeviceActivity({ device }: { device: Device }) {
  const { t } = useTranslation('live');
  if (device.status === 'PENDING_ACTIVATION' && device.activationExpiresAt) {
    return (
      <span className="inline-flex flex-wrap gap-1">
        {t('devices.activationExpires')}
        <DateTimeText value={device.activationExpiresAt} />
      </span>
    );
  }
  if (device.status === 'ACTIVE') {
    return device.lastSyncAt ? (
      <span className="inline-flex flex-wrap gap-1">
        {t('devices.lastSync')}
        <DateTimeText value={device.lastSyncAt} variant="relative" />
      </span>
    ) : (
      <span>{t('devices.neverSynced')}</span>
    );
  }
  if (device.status === 'REVOKED' && device.revokedAt) {
    return (
      <span className="inline-flex flex-wrap gap-1">
        {t('devices.revokedAt')}
        <DateTimeText value={device.revokedAt} variant="date" />
      </span>
    );
  }
  return null;
}
