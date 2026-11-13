import { useTranslation } from 'react-i18next';
import { ListRow } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { ListPanel, PageHeader, PageSection } from '@/shell/page';
import { stationApi } from '../api';
import { AgentStatusChip, DeviceStatusChip, StationStatusChip } from '../chips';
import { EmptyList, PagedList } from '../components';
import { DeviceActivity } from '../device-activity';
import { usePagedResource } from '../use-resource';

/** Station portal: the operator's stations, POS devices and agents, read only. Nothing secret is shown. */
export function StationNetworkPage() {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const stations = usePagedResource((page, signal) => stationApi.stations(page, signal), []);
  const devices = usePagedResource((page, signal) => stationApi.devices(page, signal), []);
  const agents = usePagedResource((page, signal) => stationApi.agents(page, signal), []);

  return (
    <>
      <PageHeader title={t('portal.network.title')} description={t('portal.network.description')} />
      <div className="flex flex-col gap-8">
        <PageSection title={t('portal.network.stations')}>
          <ListPanel>
            <PagedList
              resource={stations}
              empty={<EmptyList title={t('portal.network.noStations')} description={t('portal.network.noStationsHint')} />}
            >
              {(items) => (
                <div className="divide-y divide-border">
                  {items.map((station) => (
                    <ListRow
                      key={station.id}
                      title={locale === 'ar' ? station.nameAr : station.nameEn}
                      subtitle={station.fuelTypes.map((fuel) => t(`enums.fuel.${fuel}`)).join(t('common.separator'))}
                      trailing={<StationStatusChip status={station.status} />}
                    />
                  ))}
                </div>
              )}
            </PagedList>
          </ListPanel>
        </PageSection>

        <PageSection title={t('portal.network.devices')}>
          <ListPanel>
            <PagedList
              resource={devices}
              empty={<EmptyList title={t('portal.network.noDevices')} description={t('portal.network.noDevicesHint')} />}
            >
              {(items) => (
                <div className="divide-y divide-border">
                  {items.map((device) => (
                    <ListRow
                      key={device.id}
                      title={
                        <span dir="ltr" className="font-mono">
                          {device.code}
                        </span>
                      }
                      subtitle={
                        <span className="flex flex-col">
                          <span>{device.label ?? t('devices.noLabel')}</span>
                          <DeviceActivity device={device} />
                        </span>
                      }
                      trailing={<DeviceStatusChip status={device.status} />}
                    />
                  ))}
                </div>
              )}
            </PagedList>
          </ListPanel>
        </PageSection>

        <PageSection title={t('portal.network.agents')}>
          <ListPanel>
            <PagedList
              resource={agents}
              empty={<EmptyList title={t('portal.network.noAgents')} description={t('portal.network.noAgentsHint')} />}
            >
              {(items) => (
                <div className="divide-y divide-border">
                  {items.map((agent) => (
                    <ListRow
                      key={agent.id}
                      title={agent.fullName}
                      subtitle={
                        <span dir="ltr" className="font-mono">
                          {agent.workerId}
                        </span>
                      }
                      trailing={<AgentStatusChip status={agent.status} />}
                    />
                  ))}
                </div>
              )}
            </PagedList>
          </ListPanel>
        </PageSection>
      </div>
    </>
  );
}
