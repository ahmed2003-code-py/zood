import { useTranslation } from 'react-i18next';
import type { Alert, AlertSeverity } from '@/domain/types';
import { ARRIVAL_CLASS, useArrivals } from '@/features/live/use-arrivals';
import { DateTimeText, Icon, List, ListRow, Plate, type Tone } from '@/design-system';
import { TONE_ICON } from '@/design-system/tone';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { localize } from '@/lib/locale';
import { useDemoStore } from '@/mocks/store';

const SEVERITY_TONE: Record<AlertSeverity, Exclude<Tone, 'neutral' | 'brand'>> = {
  info: 'info',
  warning: 'warning',
  critical: 'danger',
};

const SEVERITY_CLASS: Record<AlertSeverity, string> = {
  info: 'bg-info-subtle text-info',
  warning: 'bg-warning-subtle text-warning',
  critical: 'bg-danger-subtle text-danger',
};

/** Alert rows: what happened (type), where (vehicle, device, station, company) and when. */
export function AlertList({ alerts, showCompany = false, onOpen }: { alerts: Alert[]; showCompany?: boolean; onOpen?: (alert: Alert) => void }) {
  const { t } = useTranslation('alerts');
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const isNew = useArrivals(alerts.map((alert) => alert.id));

  if (alerts.length === 0) return <p className="px-4 py-6 text-sm text-muted">{t('none')}</p>;

  return (
    <List>
      {alerts.map((alert) => {
        const vehicle = alert.vehicleId ? data.vehicles[alert.vehicleId] : undefined;
        const device = alert.deviceId ? data.devices[alert.deviceId] : undefined;
        const context = [
          showCompany && alert.companyId ? localize(data.companies[alert.companyId]?.name, locale) : undefined,
          !vehicle && alert.stationId ? localize(data.stations[alert.stationId]?.name, locale) : undefined,
        ].filter(Boolean);
        const glyph = TONE_ICON[SEVERITY_TONE[alert.severity]];
        return (
          <div key={alert.id} className={isNew(alert.id) ? ARRIVAL_CLASS : undefined}>
          <ListRow
            chevron={Boolean(onOpen)}
            onClick={onOpen ? () => onOpen(alert) : undefined}
            leading={
              <span className={cn('flex size-9 items-center justify-center rounded-full', SEVERITY_CLASS[alert.severity])}>
                {glyph && <Icon icon={glyph} size={18} />}
              </span>
            }
            title={t(`types.${alert.type}`)}
            subtitle={
              <span className="flex min-w-0 items-center gap-1.5">
                {vehicle && <Plate value={vehicle.plate} variant="plain" className="font-medium" />}
                {device && <span dir="ltr">{device.code}</span>}
                {context.length > 0 && <span className="truncate">{context.join(' · ')}</span>}
              </span>
            }
            trailing={<DateTimeText value={alert.at} variant="relative" className="text-xs font-normal text-muted" />}
          />
          </div>
        );
      })}
    </List>
  );
}
