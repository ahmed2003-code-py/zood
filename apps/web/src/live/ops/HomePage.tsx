import {
  Banknote,
  Building2,
  CheckCheck,
  ChevronRight,
  Fuel,
  Handshake,
  Hourglass,
  Landmark,
  LifeBuoy,
  RotateCcw,
  ShieldAlert,
  Smartphone,
  Wallet,
  type LucideIcon,
} from 'lucide-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { api } from '@/api';
import { useSession } from '@/auth/session-store';
import { CountUp, ErrorState, Icon, KpiTile, Panel, Skeleton } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatDate, formatMoney, formatNumber, formatVolume, localHour } from '@/lib/format';
import { cairoDay, periodRange } from '@/lib/periods';
import { PageHeader, PageSection } from '@/shell/page';
import { opsApi, type ActivityDay } from '../api';
import { sumActivity } from '../insights/model';
import { arrangeQueues, canActOn, countLabel, countOf, queueOrder, type QueueCount, type QueueId } from '../insights/ops-queues';
import { KpiRowSkeleton } from '../insights/ui';
import { useResource } from '../use-resource';

function greetingKey(hour: number) {
  return hour < 12 ? 'morning' : hour < 17 ? 'afternoon' : 'evening';
}

const QUEUE: Record<QueueId, { icon: LucideIcon; to: string }> = {
  approveTopUps: { icon: CheckCheck, to: '/ops/topups?status=PENDING_APPROVAL' },
  verifyTopUps: { icon: Banknote, to: '/ops/topups?status=PENDING' },
  adjustments: { icon: Wallet, to: '/ops/topups?tab=adjustments&status=PENDING_APPROVAL' },
  bankChanges: { icon: Landmark, to: '/ops/settlements?tab=bankChanges' },
  payouts: { icon: Hourglass, to: '/ops/settlements?tab=payouts' },
  reversals: { icon: RotateCcw, to: '/ops/settlements?tab=reversals' },
  alerts: { icon: ShieldAlert, to: '/ops/alerts' },
  tickets: { icon: LifeBuoy, to: '/ops/tickets' },
  leads: { icon: Handshake, to: '/ops/leads' },
  expiredDevices: { icon: Smartphone, to: '/ops/devices?status=PENDING_ACTIVATION' },
};

const QUEUE_PAGE = 100;

/** Every queue's first page, counted. One failure leaves only that queue unknown. */
function useQueueCounts() {
  return useResource(async (signal) => {
    const page = { limit: QUEUE_PAGE };
    const safe = <T,>(request: Promise<T>) => request.catch(() => undefined);
    const [approve, verify, adjustments, bank, payouts, reversals, alerts, tickets, leads, devices] = await Promise.all([
      safe(opsApi.topUps.list({ ...page, status: 'PENDING_APPROVAL' }, signal)),
      safe(opsApi.topUps.list({ ...page, status: 'PENDING' }, signal)),
      safe(opsApi.adjustments.list({ ...page, status: 'PENDING_APPROVAL' }, signal)),
      safe(opsApi.finance.bankChanges({ ...page, status: 'PENDING' }, signal)),
      safe(opsApi.finance.payouts({ ...page, status: 'PENDING' }, signal)),
      safe(opsApi.finance.reversals({ ...page, status: 'PENDING' }, signal)),
      safe(opsApi.alerts.list({ ...page, status: 'OPEN' }, signal)),
      safe(opsApi.tickets.list({ ...page, status: 'OPEN' }, signal)),
      safe(api.leads.list({ ...page, status: 'NEW' }, signal)),
      safe(opsApi.devices.list({ ...page, status: 'PENDING_ACTIVATION' }, signal)),
    ]);
    const counts: Partial<Record<QueueId, QueueCount>> = {};
    if (approve) counts.approveTopUps = countOf(approve);
    if (verify) counts.verifyTopUps = countOf(verify);
    if (adjustments) counts.adjustments = countOf(adjustments);
    if (bank) counts.bankChanges = countOf(bank);
    if (payouts) counts.payouts = countOf(payouts);
    if (reversals) counts.reversals = countOf(reversals);
    if (alerts) counts.alerts = countOf(alerts);
    if (tickets) counts.tickets = countOf(tickets);
    if (leads) counts.leads = countOf(leads);
    if (devices) counts.expiredDevices = { count: devices.items.filter((device) => device.activationExpired).length, more: Boolean(devices.nextCursor) };
    return counts;
  }, []);
}

function QueueCard({ id, count, actionable }: { id: QueueId; count?: QueueCount; actionable: boolean }) {
  const { t } = useTranslation('portal');
  const { locale } = useLocale();
  const { icon, to } = QUEUE[id];
  const urgent = count !== undefined && count.count > 0 && actionable;
  return (
    <Link
      to={to}
      className={cn(
        'group flex min-h-28 flex-col justify-between gap-3 rounded-container border bg-surface p-4 shadow-card transition-colors duration-150 hover:bg-surface-hover active:bg-surface-sunken md:p-5',
        urgent ? 'border-border-strong' : 'border-border',
      )}
    >
      <span className="flex items-start justify-between gap-3">
        <span
          className={cn(
            'flex size-9 shrink-0 items-center justify-center rounded-control',
            urgent ? 'bg-primary-subtle text-primary-subtle-foreground' : 'bg-surface-sunken text-muted',
          )}
        >
          <Icon icon={icon} size={18} />
        </span>
        <span className="text-kpi leading-none font-semibold tracking-tight tabular-nums">
          {count ? countLabel(count, (value) => formatNumber(value, locale)) : '–'}
        </span>
      </span>
      <span className="flex items-end justify-between gap-2">
        <span className="min-w-0">
          <span className="block text-sm font-semibold">{t(`opsHome.queues.${id}.title`)}</span>
          <span className="block text-xs text-pretty text-muted">
            {actionable ? t(`opsHome.queues.${id}.hint`) : t('opsHome.viewOnly')}
          </span>
        </span>
        <Icon icon={ChevronRight} size={16} directional className="shrink-0 text-muted transition-transform duration-150 group-hover:translate-x-0.5 rtl:group-hover:-translate-x-0.5" />
      </span>
    </Link>
  );
}

/** The last 14 days of spend as quiet bars, today in the full brand colour. */
function SpendBars({ days }: { days: ActivityDay[] }) {
  const { t } = useTranslation('portal');
  const { locale } = useLocale();
  const max = Math.max(1, ...days.map((day) => day.amountPiastres));
  return (
    <figure className="flex flex-col gap-2">
      {/* Plotting runs left to right in both languages (design system §8). */}
      <div dir="ltr" className="flex h-24 items-end gap-1" role="img" aria-label={t('opsHome.trendLabel')}>
        {days.map((day, index) => (
          <div
            key={day.day}
            title={`${formatDate(`${day.day}T12:00:00Z`, locale, { timeZone: 'UTC' })} · ${formatMoney(day.amountPiastres, locale)}`}
            className={cn('min-h-0.5 flex-1 rounded-t-[3px]', index === days.length - 1 ? 'bg-primary' : 'bg-primary/30')}
            style={{ height: `${Math.max(2, Math.round((day.amountPiastres * 100) / max))}%` }}
          />
        ))}
      </div>
      <figcaption dir="ltr" className="flex justify-between text-xs text-muted">
        <span>{days[0] ? formatDate(`${days[0].day}T12:00:00Z`, locale, { timeZone: 'UTC' }) : ''}</span>
        <span>{days.at(-1) ? formatDate(`${days.at(-1)!.day}T12:00:00Z`, locale, { timeZone: 'UTC' }) : ''}</span>
      </figcaption>
    </figure>
  );
}

/** Ops home (live): the work waiting for this person first, then how the platform is doing. */
export function OpsHomePage() {
  const { t } = useTranslation(['portal', 'shell']);
  const { locale } = useLocale();
  const user = useSession((state) => state.user);
  const counts = useQueueCounts();
  const window = useMemo(() => ({ ...periodRange('LAST_7_DAYS'), fortnight: periodRange('LAST_30_DAYS').from, today: cairoDay(new Date()) }), []);
  const activity = useResource((signal) => opsApi.activity({ from: window.fortnight, to: window.today }, signal), [window.today]);
  const companies = useResource((signal) => opsApi.companies.list({ limit: 100 }, signal), []);
  const stations = useResource((signal) => opsApi.stations.list({ limit: 100, status: 'ACTIVE' }, signal), []);

  const order = queueOrder(user?.role);
  const arranged = counts.data ? arrangeQueues(order, counts.data) : undefined;
  const days = activity.data ?? [];
  const today = days.filter((day) => day.day === window.today);
  const week = days.filter((day) => day.day >= window.from);
  const todayTotals = sumActivity(today);
  const weekTotals = sumActivity(week);
  const activeCompanies = companies.data?.items.filter((company) => company.status === 'ACTIVE').length;
  const firstName = user?.fullName.trim().split(/\s+/)[0] ?? '';

  return (
    <>
      <PageHeader title={t(`shell:greeting.${greetingKey(localHour(new Date()))}`, { name: firstName })} description={t('opsHome.description')} />

      <PageSection title={t('opsHome.queuesTitle')} description={t(order[0] === 'approveTopUps' ? 'opsHome.queuesHintManager' : 'opsHome.queuesHint')}>
        {counts.error && !counts.data ? (
          <ErrorState onRetry={counts.reload} className="rounded-container border border-border bg-surface" />
        ) : !arranged ? (
          <div aria-busy className="grid grid-cols-1 gap-3 sm:grid-cols-2 md:gap-4 xl:grid-cols-3">
            {[0, 1, 2, 3, 4, 5].map((index) => (
              <Skeleton key={index} className="h-28 rounded-container" />
            ))}
          </div>
        ) : (
          <>
            {arranged.open.length === 0 ? (
              <Panel>
                <p className="flex items-center gap-2 text-sm font-medium">
                  <Icon icon={CheckCheck} className="text-success" />
                  {t('opsHome.allClear')}
                </p>
              </Panel>
            ) : (
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 md:gap-4 xl:grid-cols-3">
                {arranged.open.map((id) => (
                  <QueueCard key={id} id={id} count={counts.data?.[id]} actionable={canActOn(user?.role, id)} />
                ))}
              </div>
            )}
            {arranged.clear.length > 0 && (
              <p className="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted">
                <Icon icon={CheckCheck} size={16} className="text-success" />
                <span>{t('opsHome.clearLead')}</span>
                {arranged.clear.map((id, index) => (
                  <Link key={id} to={QUEUE[id].to} className="underline-offset-2 hover:text-foreground hover:underline">
                    {t(`opsHome.queues.${id}.title`)}
                    {index < arranged.clear.length - 1 ? t('opsHome.listSeparator') : ''}
                  </Link>
                ))}
              </p>
            )}
          </>
        )}
      </PageSection>

      <PageSection className="mt-8 md:mt-10" title={t('opsHome.platformTitle')} description={t('opsHome.platformHint')}>
        {!activity.data && !activity.error ? (
          <KpiRowSkeleton />
        ) : (
          <div className="grid grid-cols-2 gap-3 md:gap-4 lg:grid-cols-4">
            <KpiTile emphasis label={t('opsHome.kpis.spendToday')} value={<CountUp value={todayTotals.amountPiastres} format={(value) => formatMoney(value, locale)} />} hint={t('opsHome.kpis.fuelingsToday', { count: todayTotals.fuelings })} />
            <KpiTile label={t('opsHome.kpis.litresToday')} value={<CountUp value={todayTotals.volumeMl} format={(value) => formatVolume(value, locale, 0)} />} hint={t('opsHome.kpis.declinedToday', { count: todayTotals.declined })} />
            <KpiTile
              label={t('opsHome.kpis.companies')}
              value={activeCompanies === undefined ? '–' : formatNumber(activeCompanies, locale) + (companies.data?.nextCursor ? '+' : '')}
              hint={<Link to="/ops/companies" className="inline-flex items-center gap-1 hover:underline"><Icon icon={Building2} size={13} />{t('opsHome.kpis.companiesLink')}</Link>}
            />
            <KpiTile
              label={t('opsHome.kpis.stations')}
              value={stations.data ? formatNumber(stations.data.items.length, locale) + (stations.data.nextCursor ? '+' : '') : '–'}
              hint={<Link to="/ops/stations" className="inline-flex items-center gap-1 hover:underline"><Icon icon={Fuel} size={13} />{t('opsHome.kpis.stationsLink')}</Link>}
            />
          </div>
        )}
        {activity.data && activity.data.length > 0 && (
          <Panel title={t('opsHome.trendTitle')} description={t('opsHome.trendHint', { amount: formatMoney(weekTotals.amountPiastres, locale), count: weekTotals.fuelings })}>
            <SpendBars days={activity.data.slice(-14)} />
          </Panel>
        )}
        {Boolean(activity.error) && !activity.data && <ErrorState onRetry={activity.reload} />}
      </PageSection>
    </>
  );
}
