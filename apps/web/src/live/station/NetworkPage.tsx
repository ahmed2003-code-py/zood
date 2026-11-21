/**
 * Station portal: the operator's POS terminals and pump agents, with their health, read only.
 * A terminal is named by its label first, then its code, and shows the station it belongs to. An
 * activation code that ran out reads "code expired, ask Operations"; a locked agent shows until
 * when. Changes go through ZAWED Operations (a ticket); nothing secret is shown.
 */
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { LifeBuoy, MapPin, Smartphone, UserRound } from 'lucide-react';
import { Button, EmptyState, ErrorState, Icon, RecordRow, RowsSkeleton, SegmentedControl, StatusChip } from '@/design-system';
import { ListPanel, PageHeader, PageSection } from '@/shell/page';
import { stationApi, type Agent, type Device } from '../api';
import { StationStatusChip } from '../chips';
import { useResource } from '../use-resource';
import { useStationName } from './hooks';
import { AgentHealthChip, DeviceHealthChip, ListFilters, Stamp, StationSelect } from './parts';
import { agentHealth, deviceHealth, type AgentHealth, type DeviceHealth } from './station-model';

type Show = 'ALL' | 'ISSUES';

const DEVICE_ISSUES: readonly DeviceHealth[] = ['OFFLINE', 'CODE_EXPIRED', 'NEVER_SYNCED'];
const AGENT_ISSUES: readonly AgentHealth[] = ['LOCKED', 'MUST_CHANGE_PIN', 'SUSPENDED'];

export function StationNetworkPage() {
  const { t } = useTranslation(['station', 'live']);
  const [stationId, setStationId] = useState('');
  const [show, setShow] = useState<Show>('ALL');
  const now = useMemo(() => new Date(), []);
  const stations = useResource((signal) => stationApi.stations({ limit: 100 }, signal), []);
  const devices = useResource((signal) => stationApi.devices({ limit: 100, stationId: stationId || undefined }, signal), [stationId]);
  const agents = useResource((signal) => stationApi.agents({ limit: 100, stationId: stationId || undefined }, signal), [stationId]);
  const stationName = useStationName(stations.data?.items);

  const deviceRows = (devices.data?.items ?? [])
    .map((device) => ({ device, health: deviceHealth(device, now) }))
    .filter((row) => show === 'ALL' || DEVICE_ISSUES.includes(row.health));
  const agentRows = (agents.data?.items ?? [])
    .map((agent) => ({ agent, health: agentHealth(agent, now) }))
    .filter((row) => show === 'ALL' || AGENT_ISSUES.includes(row.health));
  const filters = (stationId ? 1 : 0) + (show === 'ISSUES' ? 1 : 0);

  return (
    <>
      <PageHeader
        title={t('live.network.title')}
        description={t('live.network.description')}
        actions={
          <Button variant="secondary" leadingIcon={LifeBuoy} asChild>
            <Link to="/station/tickets">{t('live.network.askOps')}</Link>
          </Button>
        }
      />
      <div className="flex flex-col gap-8">
        <ListPanel>
          <ListFilters
            active={filters}
            onClear={() => {
              setStationId('');
              setShow('ALL');
            }}
          >
            <StationSelect stations={stations.data?.items ?? []} value={stationId} onChange={setStationId} />
            <SegmentedControl<Show>
              label={t('live.network.show')}
              value={show}
              onValueChange={setShow}
              options={[
                { value: 'ALL', label: t('live.network.showAll') },
                { value: 'ISSUES', label: t('live.network.showIssues') },
              ]}
            />
          </ListFilters>
          <SectionTitle icon={Smartphone} title={t('live.network.devices')} count={devices.data ? deviceRows.length : undefined} />
          {devices.error && !devices.data ? (
            <ErrorState onRetry={devices.reload} className="py-6" />
          ) : !devices.data ? (
            <RowsSkeleton />
          ) : deviceRows.length === 0 ? (
            <EmptyState
              title={t(show === 'ISSUES' ? 'live.network.noDeviceIssues' : 'live.network.noDevices')}
              description={t(show === 'ISSUES' ? 'live.network.noDeviceIssuesHint' : 'live.network.noDevicesHint')}
            />
          ) : (
            <div className="divide-y divide-border">
              {deviceRows.map(({ device, health }) => (
                <DeviceRow key={device.id} device={device} health={health} station={stationName(device.stationId)} />
              ))}
            </div>
          )}
        </ListPanel>

        <PageSection title={t('live.network.agents')} description={t('live.network.agentsHint')}>
          <ListPanel>
            {agents.error && !agents.data ? (
              <ErrorState onRetry={agents.reload} className="py-6" />
            ) : !agents.data ? (
              <RowsSkeleton />
            ) : agentRows.length === 0 ? (
              <EmptyState
                title={t(show === 'ISSUES' ? 'live.network.noAgentIssues' : 'live.network.noAgents')}
                description={t(show === 'ISSUES' ? 'live.network.noAgentIssuesHint' : 'live.network.noAgentsHint')}
              />
            ) : (
              <div className="divide-y divide-border">
                {agentRows.map(({ agent, health }) => (
                  <AgentRow key={agent.id} agent={agent} health={health} station={stationName(agent.stationId)} />
                ))}
              </div>
            )}
          </ListPanel>
        </PageSection>

        <PageSection title={t('live.network.stations')}>
          <ListPanel>
            {stations.error && !stations.data ? (
              <ErrorState onRetry={stations.reload} className="py-6" />
            ) : !stations.data ? (
              <RowsSkeleton />
            ) : stations.data.items.length === 0 ? (
              <EmptyState title={t('live:portal.network.noStations')} description={t('live:portal.network.noStationsHint')} />
            ) : (
              <div className="divide-y divide-border">
                {stations.data.items.map((station) => (
                  <RecordRow key={station.id} status={<StationStatusChip status={station.status} />}>
                    <p className="flex items-center gap-2 font-medium">
                      <Icon icon={MapPin} size={16} className="text-muted" />
                      {stationName(station.id)}
                    </p>
                    <p className="mt-0.5 text-sm text-muted">{station.fuelTypes.map((fuel) => t(`live:enums.fuel.${fuel}`)).join(t('live:common.separator'))}</p>
                  </RecordRow>
                ))}
              </div>
            )}
          </ListPanel>
        </PageSection>
      </div>
    </>
  );
}

function SectionTitle({ icon, title, count }: { icon: typeof Smartphone; title: string; count?: number }) {
  return (
    <div className="flex items-center gap-2 border-b border-border px-4 py-3 md:px-5">
      <Icon icon={icon} size={18} className="text-muted" />
      <h2 className="text-base font-semibold">{title}</h2>
      {count !== undefined && <StatusChip tone="neutral" label={String(count)} />}
    </div>
  );
}

function DeviceRow({ device, health, station }: { device: Device; health: DeviceHealth; station?: string }) {
  const { t } = useTranslation('station');
  return (
    <RecordRow status={<DeviceHealthChip health={health} />}>
      <p className="font-medium">{device.label ?? t('live.network.noLabel')}</p>
      <p className="mt-0.5 flex flex-wrap items-center gap-x-2 text-sm text-muted">
        <bdi dir="ltr" className="font-mono">
          {device.code}
        </bdi>
        {station && <span>· {station}</span>}
      </p>
      <p className="mt-0.5 text-xs text-muted">
        {health === 'CODE_EXPIRED' ? (
          t('live.network.codeExpiredHint')
        ) : health === 'AWAITING_ACTIVATION' && device.activationExpiresAt ? (
          <>
            {t('live.network.codeValidUntil')} <Stamp value={device.activationExpiresAt} />
          </>
        ) : device.lastSyncAt ? (
          <>
            {t('live.network.lastSync')} <Stamp value={device.lastSyncAt} />
          </>
        ) : health === 'NEVER_SYNCED' ? (
          t('live.network.neverSynced')
        ) : null}
      </p>
    </RecordRow>
  );
}

function AgentRow({ agent, health, station }: { agent: Agent; health: AgentHealth; station?: string }) {
  const { t } = useTranslation('station');
  return (
    <RecordRow status={<AgentHealthChip health={health} lockedUntil={agent.lockedUntil} />}>
      <p className="flex items-center gap-2 font-medium">
        <Icon icon={UserRound} size={16} className="text-muted" />
        {agent.fullName}
      </p>
      <p className="mt-0.5 flex flex-wrap items-center gap-x-2 text-sm text-muted">
        <span>
          {t('live.network.workerId')} <bdi dir="ltr" className="font-mono">{agent.workerId}</bdi>
        </span>
        {station && <span>· {station}</span>}
      </p>
      {(health === 'LOCKED' || health === 'MUST_CHANGE_PIN') && <p className="mt-0.5 text-xs text-muted">{t(`live.network.agentHint.${health}`)}</p>}
    </RecordRow>
  );
}
