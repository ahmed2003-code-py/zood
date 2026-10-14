/**
 * Station manager screens (scope: one station, PRD §3). Device status is view-only: activation and
 * binding belong to ZAWED Operations. Shift closing and payout requests are not part of the demo.
 */
import { CalendarClock, UserRound } from 'lucide-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Icon, KpiTile, List, ListRow, Money, StatusChip, Volume } from '@/design-system';
import { AlertList } from '@/features/alerts/AlertList';
import { DeviceList } from '@/features/devices/DeviceList';
import { TransactionList } from '@/features/transactions/TransactionList';
import { useLocale } from '@/i18n/useLocale';
import { formatMoney, formatNumber, formatTime, formatVolume } from '@/lib/format';
import { localize } from '@/lib/locale';
import { useNow } from '@/lib/use-now';
import { DEMO_CONTEXT } from '@/mocks/personas';
import { agentActivityToday, openAlerts, recentTransactions, stationSettlement, stationSummary } from '@/mocks/selectors';
import { useDemoStore } from '@/mocks/store';
import { ListPanel, PageHeader, PageSection, PhaseNote, ViewAllLink } from '@/shell/page';

const stationId = DEMO_CONTEXT.stationId;

export function StationHome() {
  const { t } = useTranslation(['station', 'common']);
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const now = useNow();
  const summary = useMemo(() => stationSummary(data, stationId, now), [data, now]);
  const alerts = useMemo(() => openAlerts(data, { stationId }).slice(0, 3), [data]);
  const recent = useMemo(() => recentTransactions(data, { stationId }, 5), [data]);
  const shift = useMemo(
    () =>
      agentActivityToday(data, stationId, now)
        .filter((activity) => activity.startedAt)
        .sort((a, b) => (a.startedAt ?? '').localeCompare(b.startedAt ?? ''))[0],
    [data, now],
  );

  return (
    <>
      <PageHeader title={localize(data.stations[stationId]?.name, locale)} description={t('home.description')} />
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <KpiTile emphasis className="col-span-2 lg:col-span-1" label={t('home.kpis.valueToday')} value={<Money value={summary.valueTodayPiastres} precision="whole" />} />
        <KpiTile label={t('home.kpis.litersToday')} value={<Volume value={summary.litersTodayMl} fractionDigits={0} />} />
        <KpiTile label={t('home.kpis.transactionsToday')} value={formatNumber(summary.transactionsToday, locale)} />
        <KpiTile
          className="hidden lg:flex"
          label={t('home.kpis.activeDevices')}
          value={t('home.devicesOnlineValue', { online: summary.devicesOnline, total: summary.devices.length })}
        />
      </div>
      <div className="mt-8 grid grid-cols-1 gap-8 lg:grid-cols-5">
        <div className="flex min-w-0 flex-col gap-8 lg:col-span-2">
          <PageSection title={t('home.currentShift')}>
            <div className="flex items-center gap-3 rounded-container border border-border bg-surface px-4 py-3">
              <span className="flex size-10 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
                <Icon icon={CalendarClock} size={20} />
              </span>
              {shift && shift.startedAt ? (
                <div className="min-w-0 flex-1">
                  <p className="truncate font-medium">
                    {t('home.shiftSummary', { agent: localize(shift.agent.name, locale), time: formatTime(shift.startedAt, locale) })}
                  </p>
                  <p className="text-sm text-muted">
                    {t('common:counts.transactions', { count: shift.transactions })} · {formatVolume(shift.volumeMl, locale, 0)}
                  </p>
                </div>
              ) : (
                <p className="text-sm text-muted">{t('home.noShift')}</p>
              )}
            </div>
          </PageSection>
          <PageSection title={t('home.attention')}>
            <ListPanel>
              <AlertList alerts={alerts} />
            </ListPanel>
          </PageSection>
        </div>
        <PageSection title={t('home.recent')} action={<ViewAllLink to="/station/transactions" />} className="lg:col-span-3">
          <ListPanel>
            <TransactionList transactions={recent} perspective="station" compact />
          </ListPanel>
        </PageSection>
      </div>
    </>
  );
}

export function StationTransactions() {
  const { t } = useTranslation(['station', 'navigation']);
  const data = useDemoStore((state) => state.data);
  const latest = useMemo(() => recentTransactions(data, { stationId }, 20), [data]);
  return (
    <>
      <PageHeader title={t('navigation:items.transactions')} description={t('transactions.description')} />
      <ListPanel>
        <TransactionList transactions={latest} perspective="station" />
      </ListPanel>
    </>
  );
}

export function StationDevices() {
  const { t } = useTranslation(['station', 'navigation']);
  const allDevices = useDemoStore((state) => state.data.devices);
  const devices = useMemo(() => Object.values(allDevices).filter((device) => device.stationId === stationId), [allDevices]);
  return (
    <>
      <PageHeader title={t('navigation:items.devices')} description={t('devices.description')} />
      <ListPanel>
        <DeviceList devices={devices} />
      </ListPanel>
    </>
  );
}

export function StationShifts() {
  const { t } = useTranslation(['station', 'navigation', 'common', 'pos']);
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const now = useNow();
  const activity = useMemo(() => agentActivityToday(data, stationId, now), [data, now]);
  return (
    <>
      <PageHeader title={t('navigation:items.shifts')} description={t('shifts.description')} />
      <PageSection title={t('shifts.onDuty')}>
        <ListPanel>
          <List>
            {activity.map(({ agent, transactions, volumeMl, valuePiastres, startedAt }) => (
              <ListRow
                key={agent.id}
                chevron={false}
                leading={
                  <span className="flex size-9 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
                    <Icon icon={UserRound} size={18} />
                  </span>
                }
                title={
                  <span className="flex items-center gap-2">
                    {localize(agent.name, locale)}
                    {startedAt && <StatusChip tone="success" label={t('shifts.open')} />}
                  </span>
                }
                subtitle={
                  <>
                    {t('pos:workerId', { id: agent.workerId })} ·{' '}
                    {startedAt ? t('shifts.since', { time: formatTime(startedAt, locale) }) : t('shifts.notStarted')}
                  </>
                }
                trailing={<span className="text-sm">{formatMoney(valuePiastres, locale, { precision: 'whole' })}</span>}
                trailingSub={
                  <span className="text-xs text-muted">
                    {t('common:counts.transactions', { count: transactions })} · {formatVolume(volumeMl, locale, 0)}
                  </span>
                }
              />
            ))}
          </List>
        </ListPanel>
      </PageSection>
    </>
  );
}

export function StationSettlements() {
  const { t } = useTranslation(['station', 'navigation']);
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const now = useNow();
  const settlement = useMemo(() => stationSettlement(data, stationId, now), [data, now]);
  return (
    <>
      <PageHeader title={t('navigation:items.settlements')} description={t('settlements.description')} />
      <PageSection title={t('settlements.cycle')}>
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-3">
          <KpiTile emphasis className="col-span-2 lg:col-span-1" label={t('settlements.due')} value={formatMoney(settlement.amountPiastres, locale, { precision: 'whole' })} />
          <KpiTile label={t('settlements.dispensed')} value={formatVolume(settlement.volumeMl, locale, 0)} />
          <KpiTile label={t('settlements.transactions')} value={formatNumber(settlement.transactions, locale)} />
        </div>
        <p className="text-sm text-muted">{t('settlements.vip', { volume: formatVolume(settlement.vipVolumeMl, locale, 0) })}</p>
      </PageSection>
      <div className="mt-8">
        <PhaseNote />
      </div>
    </>
  );
}
