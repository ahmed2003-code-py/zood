/**
 * Company portal screens: home, wallet, budgets, limits, alerts, reports, requests.
 * Core data is view-only for companies; changes go through requests to ZAWED Operations (PRD §3).
 */
import { ChartColumn, FileSpreadsheet, Fuel, Gauge, MessageSquareText, PlusCircle, Tag, Undo2, Wallet } from 'lucide-react';
import { lazy, Suspense, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import type { LedgerEntryType } from '@/domain/types';
import { isSamePeriod } from '@/domain/calendar';
import {
  Button,
  DateTimeText,
  Icon,
  KpiTile,
  List,
  ListRow,
  Money,
  Skeleton,
  StatusChip,
  type LucideIcon,
} from '@/design-system';
import { AlertList } from '@/features/alerts/AlertList';
import { ARRIVAL_CLASS, useArrivals } from '@/features/live/use-arrivals';
import { TransactionList } from '@/features/transactions/TransactionList';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { DEFAULT_TIME_ZONE, formatDate, formatMoney, formatNumber, formatVolume, localHour } from '@/lib/format';
import { localize } from '@/lib/locale';
import { useNow } from '@/lib/use-now';
import { DEMO_CONTEXT, PERSONAS } from '@/mocks/personas';
import { companySummary, dailySpend, departmentBudgets, monthSpend, openAlerts, recentTransactions } from '@/mocks/selectors';
import { useDemoStore } from '@/mocks/store';
import { ListPanel, PageHeader, PageSection, PhaseNote, ViewAllLink, ViewOnlyNote } from '@/shell/page';

const SpendChart = lazy(() => import('@/features/charts/SpendChart'));

const companyId = DEMO_CONTEXT.companyId;

function greetingKey(hour: number) {
  return hour < 12 ? 'morning' : hour < 17 ? 'afternoon' : 'evening';
}

/** A value that briefly highlights when it changes while the screen is open (e.g. after a POS sale). */
function LiveValue({ value, children }: { value: number; children: ReactNode }) {
  const [initial] = useState(value);
  return (
    <span key={value} className={cn('-mx-1 rounded-md px-1', value !== initial && ARRIVAL_CLASS)}>
      {children}
    </span>
  );
}

export function CompanyHome() {
  const { t } = useTranslation(['company', 'shell']);
  const { locale } = useLocale();
  const navigate = useNavigate();
  const data = useDemoStore((state) => state.data);
  const now = useNow();
  const summary = useMemo(() => companySummary(data, companyId, now), [data, now]);
  const spentThisMonth = useMemo(() => monthSpend(data, companyId, now), [data, now]);
  const alerts = useMemo(() => openAlerts(data, { companyId }), [data]);
  const recent = useMemo(() => recentTransactions(data, { companyId }, 4), [data]);
  const series = useMemo(() => dailySpend(data, companyId, now, 14), [data, now]);
  const firstName = localize(PERSONAS.company.name, locale).split(' ')[0];

  return (
    <>
      <PageHeader title={t(`shell:greeting.${greetingKey(localHour(now))}`, { name: firstName })} description={t('home.description')} />
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <KpiTile
          emphasis
          className="col-span-2 lg:col-span-1"
          label={t('home.kpis.wallet')}
          value={
            <LiveValue value={summary.balancePiastres}>
              <Money value={summary.balancePiastres} precision="whole" />
            </LiveValue>
          }
          hint={summary.lastTopup && t('home.walletHint', { date: formatDate(summary.lastTopup.at, locale) })}
        />
        <KpiTile
          label={t('home.kpis.monthSpend')}
          value={
            <LiveValue value={spentThisMonth}>
              <Money value={spentThisMonth} precision="whole" />
            </LiveValue>
          }
        />
        <KpiTile label={t('home.kpis.activeVehicles')} value={formatNumber(summary.activeVehicles, locale)} hint={t('home.activeHint', { normal: summary.activeNormal, vip: summary.activeVip })} />
        <KpiTile className="hidden lg:flex" label={t('home.kpis.openAlerts')} value={formatNumber(alerts.length, locale)} />
      </div>
      <div className="mt-8 grid grid-cols-1 gap-8 lg:grid-cols-5">
        <PageSection title={t('home.attention')} action={<ViewAllLink to="/company/alerts" />} className="lg:col-span-2">
          <ListPanel>
            <AlertList alerts={alerts.slice(0, 3)} />
          </ListPanel>
        </PageSection>
        <div className="flex min-w-0 flex-col gap-8 lg:col-span-3">
          <Suspense fallback={<Skeleton className="h-64 rounded-container" />}>
            <SpendChart title={t('home.spendChart')} seriesLabel={t('home.spend')} points={series} />
          </Suspense>
          <PageSection title={t('home.recent')} action={<ViewAllLink to="/company/transactions" />}>
            <ListPanel>
              <TransactionList transactions={recent} perspective="company" compact onOpen={(txn) => navigate(`/company/transactions/${txn.id}`)} />
            </ListPanel>
          </PageSection>
        </div>
      </div>
    </>
  );
}

const LEDGER_ICON: Record<LedgerEntryType, LucideIcon> = { topup: Wallet, fuelDebit: Fuel, reversal: Undo2 };

export function CompanyWallet() {
  const { t } = useTranslation(['company', 'navigation']);
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const now = useNow();
  const summary = useMemo(() => companySummary(data, companyId, now), [data, now]);
  const entries = useMemo(() => [...data.ledger].reverse().filter((entry) => entry.companyId === companyId), [data.ledger]);
  const isNew = useArrivals(entries.map((entry) => entry.id));
  const thisMonth = entries.filter((entry) => isSamePeriod(new Date(entry.at), now, 'month', DEFAULT_TIME_ZONE));
  const spent = -thisMonth.filter((entry) => entry.amountPiastres < 0).reduce((sum, entry) => sum + entry.amountPiastres, 0);
  const toppedUp = thisMonth.filter((entry) => entry.type === 'topup').reduce((sum, entry) => sum + entry.amountPiastres, 0);

  return (
    <>
      <PageHeader
        title={t('navigation:items.wallet')}
        description={t('wallet.description')}
        actions={
          <Button asChild variant="secondary">
            <Link to="/company/requests">{t('wallet.requestTopup')}</Link>
          </Button>
        }
      />
      <section className="mb-8">
        <p className="text-sm text-muted">{t('wallet.balance')}</p>
        <p className="mt-1 text-4xl font-semibold md:text-5xl">
          <LiveValue value={summary.balancePiastres}>
            <Money value={summary.balancePiastres} />
          </LiveValue>
        </p>
        {summary.lastTopup && (
          <p className="mt-2 text-sm text-muted">
            {t('wallet.lastTopup', {
              amount: formatMoney(summary.lastTopup.amountPiastres, locale, { precision: 'whole' }),
              date: formatDate(summary.lastTopup.at, locale),
            })}
          </p>
        )}
      </section>
      <PageSection title={t('wallet.thisMonth')} className="mb-8">
        <div className="grid grid-cols-2 gap-3 lg:max-w-xl">
          <KpiTile label={t('wallet.monthSpent')} value={<Money value={spent} precision="whole" />} />
          <KpiTile label={t('wallet.monthTopups')} value={<Money value={toppedUp} precision="whole" />} />
        </div>
      </PageSection>
      <PageSection title={t('wallet.movements')}>
        <ListPanel>
          <List>
            {entries.slice(0, 8).map((entry) => (
              <div key={entry.id} className={isNew(entry.id) ? ARRIVAL_CLASS : undefined}>
                <ListRow
                  chevron={false}
                  leading={
                    <span
                      className={cn(
                        'flex size-9 items-center justify-center rounded-full',
                        entry.amountPiastres > 0 ? 'bg-success-subtle text-success' : 'bg-surface-sunken text-muted',
                      )}
                    >
                      <Icon icon={LEDGER_ICON[entry.type]} size={18} />
                    </span>
                  }
                  title={t(`wallet.entry.${entry.type}`)}
                  subtitle={
                    <>
                      <bdi dir="ltr">{entry.reference}</bdi> · <DateTimeText value={entry.at} variant="date" />
                    </>
                  }
                  trailing={
                    <Money value={entry.amountPiastres} signDisplay="always" className={entry.amountPiastres > 0 ? 'text-success' : undefined} />
                  }
                  trailingSub={
                    <span className="text-xs text-muted">
                      <Money value={entry.balanceAfterPiastres} precision="whole" />
                    </span>
                  }
                />
              </div>
            ))}
          </List>
        </ListPanel>
      </PageSection>
    </>
  );
}

export function CompanyBudgets() {
  const { t } = useTranslation(['company', 'navigation']);
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const now = useNow();
  const budgets = useMemo(() => departmentBudgets(data, companyId, now), [data, now]);
  return (
    <>
      <PageHeader title={t('navigation:items.budgets')} description={t('budgets.description')} />
      <PageSection title={t('budgets.thisMonth')}>
        <ListPanel>
          <ul className="divide-y divide-border">
            {budgets.map(({ department, spentPiastres }) => {
              const total = department.monthlyBudgetPiastres;
              const ratio = total > 0 ? spentPiastres / total : 0;
              const percent = Math.round(ratio * 100);
              return (
                <li key={department.id} className="flex flex-col gap-2 px-4 py-4">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <span className="font-medium">{localize(department.name, locale)}</span>
                    <span className="flex items-center gap-2">
                      {ratio >= 1 ? (
                        <StatusChip tone="danger" label={t('budgets.exceeded')} />
                      ) : (
                        ratio >= 0.8 && <StatusChip tone="warning" label={t('budgets.warning')} />
                      )}
                      <span className="text-sm font-semibold tabular-nums">{formatNumber(percent, locale)}%</span>
                    </span>
                  </div>
                  <div
                    className="h-2 overflow-hidden rounded-full bg-surface-sunken"
                    role="progressbar"
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={Math.min(100, percent)}
                    aria-label={localize(department.name, locale)}
                  >
                    <div
                      className={cn('h-full rounded-full', ratio >= 1 ? 'bg-danger' : ratio >= 0.8 ? 'bg-warning' : 'bg-primary')}
                      style={{ width: `${Math.min(100, percent)}%` }}
                    />
                  </div>
                  <div className="flex flex-wrap justify-between gap-x-4 gap-y-1 text-sm text-muted">
                    <span>
                      {t('budgets.usage', {
                        used: formatMoney(spentPiastres, locale, { precision: 'whole' }),
                        total: formatMoney(total, locale, { precision: 'whole' }),
                      })}
                    </span>
                    <span>{t('budgets.remaining', { amount: formatMoney(Math.max(0, total - spentPiastres), locale, { precision: 'whole' }) })}</span>
                  </div>
                </li>
              );
            })}
          </ul>
        </ListPanel>
      </PageSection>
      <div className="mt-6 flex flex-col gap-3">
        <p className="text-sm text-muted">{t('budgets.monitoring')}</p>
        <ViewOnlyNote />
      </div>
    </>
  );
}

export function CompanyLimits() {
  const { t } = useTranslation(['company', 'navigation', 'common']);
  const { locale } = useLocale();
  const rules = useDemoStore((state) => state.data.limitRules);
  const companyRules = rules.filter((rule) => rule.companyId === companyId);
  return (
    <>
      <PageHeader
        title={t('navigation:items.limits')}
        description={t('limits.description')}
        actions={
          <Button asChild variant="secondary">
            <Link to="/company/requests">
              <Icon icon={MessageSquareText} size={18} />
              {t('limits.requestChange')}
            </Link>
          </Button>
        }
      />
      <PageSection title={t('limits.rules')}>
        <ListPanel>
          <List>
            {companyRules.map((rule) => {
              const value =
                rule.kind === 'volume' ? formatVolume(rule.maxValue, locale, 0) : formatMoney(rule.maxValue, locale, { precision: 'whole' });
              return (
                <ListRow
                  key={rule.id}
                  chevron={false}
                  leading={
                    <span className="flex size-9 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
                      <Icon icon={Gauge} size={18} />
                    </span>
                  }
                  title={t(`limits.rule.${rule.kind}_${rule.period}`, { value })}
                  subtitle={t('limits.appliesTo', { vehicles: t('common:counts.vehicles', { count: rule.vehicleIds.length }) })}
                />
              );
            })}
          </List>
        </ListPanel>
      </PageSection>
      <div className="mt-6">
        <ViewOnlyNote />
      </div>
    </>
  );
}

export function CompanyAlerts() {
  const { t } = useTranslation(['company', 'navigation']);
  const data = useDemoStore((state) => state.data);
  const alerts = useMemo(() => openAlerts(data, { companyId }), [data]);
  return (
    <>
      <PageHeader title={t('navigation:items.alerts')} description={t('alerts.description')} />
      <ListPanel>
        <AlertList alerts={alerts} />
      </ListPanel>
    </>
  );
}

function IconRow({ icon, title, subtitle }: { icon: LucideIcon; title: string; subtitle?: string }) {
  return (
    <ListRow
      chevron={false}
      leading={
        <span className="flex size-9 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
          <Icon icon={icon} size={18} />
        </span>
      }
      title={title}
      subtitle={subtitle}
    />
  );
}

export function CompanyReports() {
  const { t } = useTranslation(['company', 'navigation']);
  return (
    <>
      <PageHeader title={t('navigation:items.reports')} description={t('reports.description')} />
      <ListPanel>
        <List>
          <IconRow icon={FileSpreadsheet} title={t('reports.monthly')} subtitle={t('reports.monthlyHint')} />
          <IconRow icon={ChartColumn} title={t('reports.departments')} subtitle={t('reports.departmentsHint')} />
        </List>
      </ListPanel>
      <div className="mt-8">
        <PhaseNote />
      </div>
    </>
  );
}

export function CompanyRequests() {
  const { t } = useTranslation(['company', 'navigation']);
  return (
    <>
      <PageHeader title={t('navigation:items.requests')} description={t('requests.description')} />
      <PageSection title={t('requests.types')}>
        <ListPanel>
          <List>
            <IconRow icon={PlusCircle} title={t('requests.type.addVehicle')} />
            <IconRow icon={Gauge} title={t('requests.type.changeLimit')} />
            <IconRow icon={Wallet} title={t('requests.type.topup')} />
            <IconRow icon={Tag} title={t('requests.type.replaceTag')} />
          </List>
        </ListPanel>
      </PageSection>
      <div className="mt-8">
        <PhaseNote />
      </div>
    </>
  );
}
