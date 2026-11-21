import { Download, Landmark, LifeBuoy, Wallet as WalletIcon } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { useSession } from '@/auth/session-store';
import { Banner, Button, CountUp, DateTimeText, EmptyState, ErrorState, Icon, KpiTile, ListRow, Money, Panel, Plate, Skeleton, Volume } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatMoney, formatNumber, formatVolume, localHour } from '@/lib/format';
import { periodRange } from '@/lib/periods';
import { PageHeader, ViewAllLink } from '@/shell/page';
import { companyApi, type Fueling, type Vehicle } from '../api';
import { FuelingStatusChip } from '../chips';
import { AlertRow } from '../fuelings';
import { budgetRows, coverLevel, daysOfCover, spendByDepartment, spendByVehicle, sumActivity } from '../insights/model';
import { KpiRowSkeleton, MeterRowsSkeleton, PartialNote, UsageMeter } from '../insights/ui';
import { activityWindow, useCompanyDepartments, useCompanyFuelings, useCompanyVehicles } from '../insights/use-company-data';
import { useStatementExport } from '../statement-export';
import { useResource } from '../use-resource';
import { TopUpRow, TransferSheet } from '../wallet';

function greetingKey(hour: number) {
  return hour < 12 ? 'morning' : hour < 17 ? 'afternoon' : 'evening';
}

/** On the green card a short cover reads as a chip, so it is never colour on green alone. */
const COVER_CHIP = { low: 'bg-warning-subtle text-warning', critical: 'bg-danger-subtle text-danger' } as const;

/** The balance, how long it lasts at the recent pace, and the money actions next to it. */
function BalanceHero({ onTopUp }: { onTopUp: () => void }) {
  const { t } = useTranslation(['portal', 'live']);
  const { locale } = useLocale();
  const window = useMemo(() => activityWindow(), []);
  const wallet = useResource((signal) => companyApi.wallet(signal), []);
  const activity = useResource((signal) => companyApi.activity({ from: window.last30.from, to: window.to }, signal), [window.from]);
  if (wallet.error && !wallet.data) return <ErrorState onRetry={wallet.reload} className="rounded-container border border-border bg-surface" />;
  if (!wallet.data) return <Skeleton className="h-44 rounded-container" />;
  const cover = activity.data ? daysOfCover(wallet.data.balancePiastres, activity.data) : undefined;
  const level = coverLevel(cover);
  return (
    <section
      aria-label={t('live:wallet.balance')}
      className="flex h-full flex-col justify-between gap-5 rounded-container bg-brand-card p-5 text-brand-card-foreground md:p-6"
    >
      <div className="min-w-0">
        <p className="flex items-center gap-2 text-sm text-brand-card-muted">
          <Icon icon={WalletIcon} size={18} />
          {t('live:wallet.balance')}
        </p>
        <p className="mt-2 text-3xl font-semibold tracking-tight tabular-nums md:text-display">
          <CountUp value={wallet.data.balancePiastres} format={(value) => formatMoney(value, locale)} />
        </p>
        <p className="mt-2 flex items-center gap-2 text-sm text-brand-card-muted">
          <span aria-hidden className="h-1 w-4 shrink-0 rounded-full bg-accent" />
          <span className={cn(level !== 'good' && cn('rounded-full px-2.5 py-0.5 text-xs font-semibold', COVER_CHIP[level]))}>
            {activity.loading && !activity.data
              ? t('companyHome.coverLoading')
              : cover === undefined
                ? t('companyHome.coverNone')
                : t(level === 'good' ? 'companyHome.cover' : 'companyHome.coverShort', { count: cover })}
          </span>
        </p>
      </div>
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" leadingIcon={Landmark} onClick={onTopUp}>
          {t('companyHome.actions.topUp')}
        </Button>
        <Button asChild variant="ghost" className="text-brand-card-foreground hover:bg-brand-card-foreground/10 hover:text-brand-card-foreground">
          <Link to="/company/wallet">{t('companyHome.actions.wallet')}</Link>
        </Button>
      </div>
    </section>
  );
}

/** One-tap services: the things a fleet manager or accountant comes to do. */
function QuickActions({ onTopUp }: { onTopUp: () => void }) {
  const { t } = useTranslation('portal');
  const navigate = useNavigate();
  const exporter = useStatementExport((page, signal) => companyApi.statement(page, signal));
  const item = 'flex min-h-14 w-full items-center gap-3 px-4 text-start text-sm font-medium transition-colors duration-150 hover:bg-surface-hover active:bg-surface-sunken md:px-5';
  const glyph = 'flex size-9 shrink-0 items-center justify-center rounded-control bg-primary-subtle text-primary-subtle-foreground';
  return (
    <Panel title={t('companyHome.actions.title')} flush className="h-full">
      <ul className="divide-y divide-border">
        <li>
          <button type="button" className={item} onClick={onTopUp}>
            <span className={glyph}>
              <Icon icon={Landmark} size={18} />
            </span>
            <span className="flex-1">{t('companyHome.actions.topUp')}</span>
          </button>
        </li>
        <li>
          <button type="button" className={item} disabled={exporter.busy} onClick={() => void exporter.run(periodRange('THIS_MONTH'))}>
            <span className={glyph}>
              <Icon icon={Download} size={18} />
            </span>
            <span className="flex-1">{t('companyHome.actions.statement')}</span>
            {exporter.busy && <span className="text-xs text-muted">{t('companyHome.actions.preparing')}</span>}
          </button>
        </li>
        <li>
          <button type="button" className={item} onClick={() => navigate('/company/tickets?new=1')}>
            <span className={glyph}>
              <Icon icon={LifeBuoy} size={18} />
            </span>
            <span className="flex-1">{t('companyHome.actions.ticket')}</span>
          </button>
        </li>
      </ul>
    </Panel>
  );
}

function plateOf(vehicles: Vehicle[] | undefined, id: string): string | undefined {
  return vehicles?.find((vehicle) => vehicle.id === id)?.plate;
}

/** Company portal home (live): money first, then this month's fuel, then what needs a look. */
export function CompanyHomePage() {
  const { t } = useTranslation(['portal', 'live', 'shell']);
  const { locale } = useLocale();
  const navigate = useNavigate();
  const user = useSession((state) => state.user);
  const [requesting, setRequesting] = useState(false);
  const window = useMemo(() => activityWindow(), []);
  const month = window.month;
  const profile = useResource((signal) => companyApi.profile(signal), []);
  const monthActivity = useResource((signal) => companyApi.activity({ from: month.from, to: month.to }, signal), [month.from]);
  const fuelings = useCompanyFuelings(month);
  const vehicles = useCompanyVehicles();
  const departments = useCompanyDepartments();
  const alerts = useResource((signal) => companyApi.alerts({ status: 'OPEN', limit: 3 }, signal), []);
  const pendingTopUps = useResource(
    (signal) =>
      Promise.all([companyApi.topUps({ status: 'PENDING', limit: 5 }, signal), companyApi.topUps({ status: 'PENDING_APPROVAL', limit: 5 }, signal)]).then(
        ([waiting, approving]) => [...waiting.items, ...approving.items].sort((a, b) => b.requestedAt.localeCompare(a.requestedAt)).slice(0, 4),
      ),
    [],
  );
  const recent = useResource((signal) => companyApi.fuelings({ limit: 5 }, signal), []);

  const totals = monthActivity.data ? sumActivity(monthActivity.data) : undefined;
  const budgets = useMemo(
    () => (fuelings.data && vehicles.data && departments.data ? budgetRows(departments.data.items, spendByDepartment(fuelings.data.items, vehicles.data.items)) : undefined),
    [fuelings.data, vehicles.data, departments.data],
  );
  const totalBudget = departments.data?.items.reduce((sum, department) => sum + (department.active ? (department.monthlyBudgetPiastres ?? 0) : 0), 0) ?? 0;
  const topVehicles = useMemo(() => (fuelings.data ? spendByVehicle(fuelings.data.items).slice(0, 5) : undefined), [fuelings.data]);
  const firstName = user?.fullName.trim().split(/\s+/)[0] ?? '';
  const greeting = t(`shell:greeting.${greetingKey(localHour(new Date()))}`, { name: firstName });

  return (
    <>
      <PageHeader title={greeting} description={t('companyHome.description')} />
      {profile.data?.status === 'FROZEN' && (
        <Banner tone="danger" className="mb-6" title={t('companyHome.frozen')} description={t('companyHome.frozenHint')} />
      )}

      <div className="grid grid-cols-1 gap-4 md:gap-5 lg:grid-cols-3">
        <div className="lg:col-span-2">
          <BalanceHero onTopUp={() => setRequesting(true)} />
        </div>
        <QuickActions onTopUp={() => setRequesting(true)} />
      </div>

      <section aria-label={t('companyHome.kpis.label')} className="mt-6 md:mt-8">
        {!totals && !monthActivity.error ? (
          <KpiRowSkeleton />
        ) : (
          <div className="grid grid-cols-2 gap-3 md:gap-4 lg:grid-cols-4">
            <KpiTile
              emphasis
              label={t('companyHome.kpis.spend')}
              value={<CountUp value={totals?.amountPiastres ?? 0} format={(value) => formatMoney(value, locale)} />}
              hint={
                totalBudget > 0 && totals
                  ? t('companyHome.kpis.ofBudget', { percent: formatNumber(Math.round((totals.amountPiastres * 100) / totalBudget), locale), budget: formatMoney(totalBudget, locale) })
                  : t('companyHome.kpis.monthSoFar')
              }
            />
            <KpiTile
              label={t('companyHome.kpis.litres')}
              value={<CountUp value={totals?.volumeMl ?? 0} format={(value) => formatVolume(value, locale, 0)} />}
              hint={t('companyHome.kpis.monthSoFar')}
            />
            <KpiTile
              label={t('companyHome.kpis.fuelings')}
              value={<CountUp value={totals?.fuelings ?? 0} format={(value) => formatNumber(value, locale)} />}
              hint={totals && totals.declined > 0 ? t('companyHome.kpis.declined', { count: totals.declined }) : t('companyHome.kpis.noDeclines')}
            />
            <KpiTile
              label={t('companyHome.kpis.alerts')}
              value={formatNumber(alerts.data?.items.length ?? 0, locale) + (alerts.data?.nextCursor ? '+' : '')}
              hint={<Link className="underline-offset-2 hover:underline" to="/company/alerts">{t('companyHome.kpis.alertsLink')}</Link>}
            />
          </div>
        )}
      </section>

      <div className="mt-6 grid grid-cols-1 gap-4 md:mt-8 md:gap-5 lg:grid-cols-2">
        <Panel
          flush
          title={t('companyHome.budgets.title')}
          description={t('companyHome.budgets.description')}
          action={<ViewAllLink to="/company/budgets" />}
        >
          {!budgets ? (
            fuelings.error || departments.error ? (
              <ErrorState onRetry={() => (fuelings.reload(), departments.reload())} />
            ) : (
              <MeterRowsSkeleton />
            )
          ) : budgets.length === 0 ? (
            <EmptyState title={t('budgets.empty')} description={t('budgets.emptyHint')} />
          ) : (
            <ul className="divide-y divide-border">
              {budgets.slice(0, 4).map((row) => (
                <li key={row.key} className="flex flex-col gap-2 px-4 py-3.5 md:px-5">
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="min-w-0 truncate text-sm font-medium">{row.department?.name ?? t('insights.unassigned')}</span>
                    <span className="shrink-0 text-sm tabular-nums">
                      <Money value={row.spentPiastres} />
                      {row.budgetPiastres !== undefined && (
                        <span className="text-muted"> {t('insights.ofAmount', { amount: formatMoney(row.budgetPiastres, locale) })}</span>
                      )}
                    </span>
                  </div>
                  {row.budgetPiastres !== undefined ? (
                    <UsageMeter
                      percent={row.percent ?? 0}
                      state={row.state}
                      label={t('insights.budgetMeter', { name: row.department?.name ?? t('insights.unassigned'), percent: row.percent ?? 0 })}
                    />
                  ) : (
                    <span className="text-xs text-muted">{t('insights.budgetState.none')}</span>
                  )}
                </li>
              ))}
            </ul>
          )}
          {fuelings.data?.truncated && (
            <div className="border-t border-border px-4 py-3 md:px-5">
              <PartialNote count={fuelings.data.items.length} />
            </div>
          )}
        </Panel>

        <Panel flush title={t('companyHome.topVehicles.title')} description={t('companyHome.topVehicles.description')} action={<ViewAllLink to="/company/reports" />}>
          {!topVehicles ? (
            fuelings.error ? (
              <ErrorState onRetry={fuelings.reload} />
            ) : (
              <MeterRowsSkeleton rows={4} />
            )
          ) : topVehicles.length === 0 ? (
            <EmptyState title={t('companyHome.topVehicles.empty')} description={t('companyHome.topVehicles.emptyHint')} />
          ) : (
            <ul className="divide-y divide-border">
              {topVehicles.map((row, index) => {
                const plate = plateOf(vehicles.data?.items, row.key);
                const share = topVehicles[0] && topVehicles[0].amountPiastres > 0 ? Math.round((row.amountPiastres * 100) / topVehicles[0].amountPiastres) : 0;
                return (
                  <li key={row.key}>
                    <Link
                      to={`/company/vehicles/${row.key}`}
                      className="flex flex-col gap-2 px-4 py-3 transition-colors duration-150 hover:bg-surface-hover md:px-5"
                    >
                      <span className="flex items-center justify-between gap-3">
                        <span className="flex min-w-0 items-center gap-2.5">
                          <span className="w-4 shrink-0 text-xs text-muted tabular-nums">{index + 1}</span>
                          {plate ? <Plate value={plate} size="sm" /> : <span className="text-sm text-muted">{t('insights.vehicle')}</span>}
                        </span>
                        <span className="flex shrink-0 items-baseline gap-2 text-sm">
                          <span className="font-semibold tabular-nums">
                            <Money value={row.amountPiastres} />
                          </span>
                          <span className="hidden text-xs text-muted sm:inline">
                            <Volume value={row.volumeMl} fractionDigits={0} />
                          </span>
                        </span>
                      </span>
                      <UsageMeter percent={share} state="ok" label={t('insights.shareMeter', { percent: share })} className="h-1.5" />
                    </Link>
                  </li>
                );
              })}
            </ul>
          )}
        </Panel>
      </div>

      <div className="mt-6 grid grid-cols-1 gap-4 md:mt-8 md:gap-5 lg:grid-cols-3">
        <Panel flush title={t('companyHome.alerts.title')} action={<ViewAllLink to="/company/alerts" />}>
          {!alerts.data ? (
            alerts.error ? <ErrorState onRetry={alerts.reload} /> : <MeterRowsSkeleton rows={2} />
          ) : alerts.data.items.length === 0 ? (
            <EmptyState title={t('companyHome.alerts.empty')} description={t('companyHome.alerts.emptyHint')} />
          ) : (
            <div className="divide-y divide-border">
              {alerts.data.items.map((alert) => (
                <AlertRow key={alert.id} alert={alert} fuelingPath="/company/transactions" />
              ))}
            </div>
          )}
        </Panel>

        <Panel flush title={t('companyHome.topUps.title')} action={<ViewAllLink to="/company/wallet" />}>
          {!pendingTopUps.data ? (
            pendingTopUps.error ? <ErrorState onRetry={pendingTopUps.reload} /> : <MeterRowsSkeleton rows={2} />
          ) : pendingTopUps.data.length === 0 ? (
            <EmptyState
              title={t('companyHome.topUps.empty')}
              description={t('companyHome.topUps.emptyHint')}
              action={
                <Button variant="secondary" size="sm" leadingIcon={Landmark} onClick={() => setRequesting(true)}>
                  {t('companyHome.actions.topUp')}
                </Button>
              }
            />
          ) : (
            <div className="divide-y divide-border">
              {pendingTopUps.data.map((topUp) => (
                <TopUpRow key={topUp.id} topUp={topUp} />
              ))}
            </div>
          )}
        </Panel>

        <Panel flush title={t('companyHome.recent.title')} action={<ViewAllLink to="/company/transactions" />}>
          {!recent.data ? (
            recent.error ? <ErrorState onRetry={recent.reload} /> : <MeterRowsSkeleton rows={3} />
          ) : recent.data.items.length === 0 ? (
            <EmptyState title={t('companyHome.recent.empty')} description={t('companyHome.recent.emptyHint')} />
          ) : (
            <div className="divide-y divide-border">
              {recent.data.items.map((fueling: Fueling) => (
                <ListRow
                  key={fueling.id}
                  title={<Plate value={fueling.plate} size="sm" />}
                  subtitle={
                    <>
                      {locale === 'ar' ? fueling.stationNameAr : fueling.stationNameEn}
                      {' · '}
                      <DateTimeText value={fueling.evaluatedAt} variant="relative" />
                    </>
                  }
                  trailing={fueling.status === 'COMPLETED' ? <Money value={fueling.amountPiastres} /> : <FuelingStatusChip status={fueling.status} declineReason={fueling.declineReason} />}
                  onClick={() => navigate(`/company/transactions/${fueling.id}`)}
                />
              ))}
            </div>
          )}
        </Panel>
      </div>

      {requesting && (
        <TransferSheet
          title={t('live:topUps.requestTitle')}
          description={t('live:topUps.requestHint')}
          submitLabel={t('live:topUps.submit')}
          doneMessage={t('live:topUps.requested')}
          send={(body) => companyApi.requestTopUp(body)}
          onClose={() => setRequesting(false)}
          onDone={pendingTopUps.reload}
        />
      )}
    </>
  );
}
