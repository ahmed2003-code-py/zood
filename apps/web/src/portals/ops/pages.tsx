/**
 * ZAWED Operations screens (D1): structure plus a representative slice of seeded data.
 * Alert investigation, top-up processing and sync controls arrive in D5/D7.
 */
import { Banknote, CircleCheck, Clock, Fuel, ScrollText, TriangleAlert } from 'lucide-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import type { Company } from '@/domain/types';
import { DataView, EmptyState, Icon, KpiTile, List, ListRow, Money, StatusChip, type DataColumn } from '@/design-system';
import { AlertList } from '@/features/alerts/AlertList';
import { DeviceList } from '@/features/devices/DeviceList';
import { TransactionList } from '@/features/transactions/TransactionList';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatMoney, formatNumber, formatRelative, formatVolume, localHour } from '@/lib/format';
import { localize } from '@/lib/locale';
import { useNow } from '@/lib/use-now';
import { DEMO_CONTEXT, PERSONAS } from '@/mocks/personas';
import { isToday, openAlerts, platformSummary, recentTransactions, stationSettlement } from '@/mocks/selectors';
import { useDemoStore } from '@/mocks/store';
import { ListPanel, NavTabs, PageHeader, PageSection, PhaseNote, ViewAllLink } from '@/shell/page';
import { OPS_NETWORK_PATHS } from './config';

function greetingKey(hour: number) {
  return hour < 12 ? 'morning' : hour < 17 ? 'afternoon' : 'evening';
}

export function OpsHome() {
  const { t } = useTranslation(['ops', 'shell']);
  const { locale } = useLocale();
  const navigate = useNavigate();
  const data = useDemoStore((state) => state.data);
  const pending = useDemoStore((state) => state.posQueue.length);
  const now = useNow();
  const summary = useMemo(() => platformSummary(data, now), [data, now]);
  const alerts = useMemo(() => openAlerts(data).slice(0, 4), [data]);
  const activity = useMemo(() => recentTransactions(data, {}, 6), [data]);
  const devices = Object.values(data.devices);
  const firstName = localize(PERSONAS.ops.name, locale).split(' ')[0];
  const transactionsToday = data.transactions.filter((txn) => txn.status === 'completed' && isToday(txn.deviceAt, now)).length;

  return (
    <>
      <PageHeader title={t(`shell:greeting.${greetingKey(localHour(now))}`, { name: firstName })} description={t('home.description')} />
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <KpiTile
          emphasis
          className="col-span-2 lg:col-span-1"
          label={t('home.kpis.volumeToday')}
          value={<Money value={summary.volumeTodayPiastres} precision="whole" />}
        />
        <KpiTile label={t('home.kpis.transactionsToday')} value={formatNumber(transactionsToday, locale)} />
        <KpiTile
          label={t('home.kpis.posOnline')}
          value={t('home.posOnlineValue', { online: devices.filter((device) => device.status === 'online').length, total: devices.length })}
        />
        <KpiTile className="hidden lg:flex" label={t('home.kpis.pendingSync')} value={formatNumber(pending, locale)} />
      </div>
      <div className="mt-8 grid grid-cols-1 gap-8 lg:grid-cols-5">
        <div className="flex min-w-0 flex-col gap-8 lg:col-span-2">
          <PageSection title={t('home.attention')} action={<ViewAllLink to="/ops/alerts" />}>
            <ListPanel>
              <AlertList alerts={alerts} showCompany onOpen={(alert) => navigate(`/ops/alerts/${alert.id}`)} />
            </ListPanel>
          </PageSection>
          <PageSection title={t('home.reconciliation')}>
            <div className="flex items-center gap-3 rounded-container border border-border bg-surface px-4 py-3">
              <Icon icon={summary.reconciled ? CircleCheck : TriangleAlert} className={summary.reconciled ? 'text-success' : 'text-danger'} />
              <span className="text-sm font-medium">{summary.reconciled ? t('home.reconciled') : t('home.mismatch')}</span>
            </div>
          </PageSection>
        </div>
        <PageSection title={t('home.activity')} action={<ViewAllLink to="/ops/transactions" />} className="lg:col-span-3">
          <ListPanel>
            <TransactionList transactions={activity} perspective="ops" compact onOpen={(txn) => navigate(`/ops/transactions/${txn.id}`)} />
          </ListPanel>
        </PageSection>
      </div>
    </>
  );
}

export function OpsCompanies() {
  const { t } = useTranslation(['ops', 'navigation', 'common']);
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const companies = useMemo(() => Object.values(data.companies), [data.companies]);
  const vehicleCount = (company: Company) => Object.values(data.vehicles).filter((vehicle) => vehicle.companyId === company.id).length;
  const statusChip = (company: Company) => (
    <StatusChip tone={company.status === 'active' ? 'success' : 'warning'} label={t(`companies.status.${company.status}`)} />
  );
  const columns: DataColumn<Company>[] = [
    { id: 'company', header: t('companies.columns.company'), cell: (company) => <span className="font-medium">{localize(company.name, locale)}</span> },
    {
      id: 'balance',
      header: t('companies.columns.balance'),
      align: 'end',
      cell: (company) => <Money value={data.wallets[company.id]?.balancePiastres ?? 0} precision="whole" />,
    },
    { id: 'vehicles', header: t('companies.columns.vehicles'), align: 'end', cell: (company) => formatNumber(vehicleCount(company), locale) },
    { id: 'status', header: t('companies.columns.status'), cell: statusChip },
  ];
  return (
    <>
      <PageHeader title={t('navigation:items.companies')} description={t('companies.description')} />
      <ListPanel>
        <DataView
          caption={t('navigation:items.companies')}
          rows={companies}
          columns={columns}
          getRowId={(company) => company.id}
          tableFrom="md"
          renderListItem={(company) => (
            <ListRow
              chevron={false}
              title={localize(company.name, locale)}
              subtitle={t('common:counts.vehicles', { count: vehicleCount(company) })}
              trailing={<Money value={data.wallets[company.id]?.balancePiastres ?? 0} precision="whole" />}
              trailingSub={statusChip(company)}
            />
          )}
        />
      </ListPanel>
      <div className="mt-8">
        <PhaseNote />
      </div>
    </>
  );
}

export function OpsTopups() {
  const { t } = useTranslation(['ops', 'navigation']);
  return (
    <>
      <PageHeader title={t('navigation:items.topups')} description={t('topups.description')} />
      <ListPanel>
        <EmptyState icon={Banknote} title={t('topups.empty')} description={t('topups.emptyHint')} />
      </ListPanel>
      <div className="mt-8">
        <PhaseNote />
      </div>
    </>
  );
}

function NetworkTabs() {
  const { t } = useTranslation('navigation');
  return (
    <NavTabs
      label={t('items.network')}
      items={[
        { to: OPS_NETWORK_PATHS.stations, label: t('items.stations') },
        { to: OPS_NETWORK_PATHS.devices, label: t('items.devices') },
        { to: OPS_NETWORK_PATHS.sync, label: t('items.sync') },
      ]}
    />
  );
}

export function OpsStations() {
  const { t } = useTranslation(['ops', 'navigation']);
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const stations = Object.values(data.stations);
  return (
    <>
      <NetworkTabs />
      <PageHeader title={t('navigation:items.stations')} description={t('stations.description')} />
      <ListPanel>
        <List>
          {stations.map((station) => {
            const devices = Object.values(data.devices).filter((device) => device.stationId === station.id);
            return (
              <ListRow
                key={station.id}
                chevron={false}
                leading={
                  <span className="flex size-9 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
                    <Icon icon={Fuel} size={18} />
                  </span>
                }
                title={localize(station.name, locale)}
                subtitle={localize(data.operators[station.operatorId]?.name, locale)}
                trailing={
                  <span className="text-sm font-normal">
                    {t('stations.devicesOnline', { online: devices.filter((device) => device.status === 'online').length, total: devices.length })}
                  </span>
                }
                trailingSub={
                  <StatusChip
                    tone={station.connectivity === 'low' ? 'warning' : 'neutral'}
                    label={t(`stations.connectivity.${station.connectivity}`)}
                  />
                }
              />
            );
          })}
        </List>
      </ListPanel>
      <div className="mt-8">
        <PhaseNote />
      </div>
    </>
  );
}

export function OpsDevices() {
  const { t } = useTranslation(['ops', 'navigation']);
  const devices = useDemoStore((state) => state.data.devices);
  return (
    <>
      <NetworkTabs />
      <PageHeader title={t('navigation:items.devices')} description={t('devices.description')} />
      <ListPanel>
        <DeviceList devices={Object.values(devices)} showStation />
      </ListPanel>
      <div className="mt-8">
        <PhaseNote />
      </div>
    </>
  );
}

export function OpsSync() {
  const { t } = useTranslation(['ops', 'navigation', 'station']);
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const queue = useDemoStore((state) => state.posQueue);
  const now = useNow();
  const devices = Object.values(data.devices);
  const demoDevice = data.devices[DEMO_CONTEXT.posDeviceId];
  return (
    <>
      <NetworkTabs />
      <PageHeader title={t('navigation:items.sync')} description={t('sync.description')} />
      <div className={cn('mb-4 flex items-center gap-2 text-sm font-medium', queue.length > 0 ? 'text-warning' : 'text-success')}>
        <Icon icon={queue.length > 0 ? Clock : CircleCheck} size={18} />
        {queue.length > 0 ? t('sync.pendingDevice', { count: queue.length, device: demoDevice?.code ?? '' }) : t('sync.allSynced')}
      </div>
      <ListPanel>
        <List>
          {devices.map((device) => {
            const pending = device.id === DEMO_CONTEXT.posDeviceId ? queue.length : 0;
            return (
              <ListRow
                key={device.id}
                chevron={false}
                title={<span dir="ltr">{device.code}</span>}
                subtitle={`${localize(data.stations[device.stationId]?.name, locale)} · ${t('sync.lastSync', { time: formatRelative(device.lastSyncAt, locale, now) })}`}
                trailing={
                  pending > 0 ? (
                    <StatusChip tone="warning" icon={Clock} label={formatNumber(pending, locale)} />
                  ) : (
                    <StatusChip tone={device.status === 'online' ? 'success' : 'warning'} label={t(`station:devices.status.${device.status}`)} />
                  )
                }
              />
            );
          })}
        </List>
      </ListPanel>
    </>
  );
}

export function OpsTransactions() {
  const { t } = useTranslation(['ops', 'navigation']);
  const navigate = useNavigate();
  const data = useDemoStore((state) => state.data);
  const latest = useMemo(() => recentTransactions(data, {}, 25), [data]);
  return (
    <>
      <PageHeader title={t('navigation:items.transactions')} description={t('transactions.description')} />
      <ListPanel>
        <TransactionList transactions={latest} perspective="ops" onOpen={(txn) => navigate(`/ops/transactions/${txn.id}`)} />
      </ListPanel>
    </>
  );
}

export function OpsAlerts() {
  const { t } = useTranslation(['ops', 'navigation']);
  const navigate = useNavigate();
  const data = useDemoStore((state) => state.data);
  const alerts = useMemo(() => openAlerts(data), [data]);
  return (
    <>
      <PageHeader title={t('navigation:items.alerts')} description={t('alerts.description')} />
      <ListPanel>
        <AlertList alerts={alerts} showCompany onOpen={(alert) => navigate(`/ops/alerts/${alert.id}`)} />
      </ListPanel>
    </>
  );
}

export function OpsSettlements() {
  const { t } = useTranslation(['ops', 'navigation']);
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const now = useNow();
  const rows = useMemo(
    () => Object.values(data.stations).map((station) => ({ station, settlement: stationSettlement(data, station.id, now) })),
    [data, now],
  );
  return (
    <>
      <PageHeader title={t('navigation:items.settlements')} description={t('settlements.description')} />
      <ListPanel>
        <List>
          {rows.map(({ station, settlement }) => (
            <ListRow
              key={station.id}
              chevron={false}
              title={localize(station.name, locale)}
              subtitle={formatVolume(settlement.volumeMl, locale, 0)}
              trailing={formatMoney(settlement.amountPiastres, locale, { precision: 'whole' })}
            />
          ))}
        </List>
      </ListPanel>
      <div className="mt-8">
        <PhaseNote />
      </div>
    </>
  );
}

export function OpsAudit() {
  const { t } = useTranslation(['ops', 'navigation']);
  return (
    <>
      <PageHeader title={t('navigation:items.audit')} description={t('audit.description')} />
      <ListPanel>
        <EmptyState icon={ScrollText} title={t('audit.empty')} description={t('audit.emptyHint')} />
      </ListPanel>
    </>
  );
}
