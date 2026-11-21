import { LifeBuoy, PiggyBank } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, CountUp, EmptyState, ErrorState, Icon, KpiTile, Money, SegmentedControl, StatusChip, Volume, type Tone } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatMoney } from '@/lib/format';
import { periodRange } from '@/lib/periods';
import { ListPanel, PageHeader, PageSection, ViewOnlyNote } from '@/shell/page';
import { budgetRows, spendByDepartment, type BudgetState } from '../insights/model';
import { KpiRowSkeleton, MeterRowsSkeleton, PartialNote, UsageMeter } from '../insights/ui';
import { useCompanyDepartments, useCompanyFuelings, useCompanyVehicles } from '../insights/use-company-data';

type Month = 'THIS_MONTH' | 'LAST_MONTH';

const STATE_TONE: Record<BudgetState, Tone> = { over: 'danger', near: 'warning', ok: 'success', none: 'neutral' };

/** Company portal: each department's monthly budget against what its vehicles spent. */
export function CompanyBudgetsPage() {
  const { t } = useTranslation(['portal', 'live']);
  const { locale } = useLocale();
  const [month, setMonth] = useState<Month>('THIS_MONTH');
  const range = useMemo(() => periodRange(month), [month]);
  const fuelings = useCompanyFuelings(range);
  const vehicles = useCompanyVehicles();
  const departments = useCompanyDepartments();
  const rows = useMemo(
    () => (fuelings.data && vehicles.data && departments.data ? budgetRows(departments.data.items, spendByDepartment(fuelings.data.items, vehicles.data.items)) : undefined),
    [fuelings.data, vehicles.data, departments.data],
  );
  const failed = Boolean(fuelings.error || vehicles.error || departments.error);
  const reload = () => {
    fuelings.reload();
    vehicles.reload();
    departments.reload();
  };
  const totalBudget = rows?.reduce((sum, row) => sum + (row.budgetPiastres ?? 0), 0) ?? 0;
  const totalSpent = rows?.reduce((sum, row) => sum + row.spentPiastres, 0) ?? 0;
  const left = totalBudget - totalSpent;

  return (
    <>
      <PageHeader title={t('budgets.title')} description={t('budgets.description')} />
      <SegmentedControl<Month>
        className="mb-5"
        label={t('budgets.month')}
        value={month}
        onValueChange={setMonth}
        options={[
          { value: 'THIS_MONTH', label: t('live:periods.THIS_MONTH') },
          { value: 'LAST_MONTH', label: t('live:periods.LAST_MONTH') },
        ]}
      />
      {!rows && !failed ? (
        <KpiRowSkeleton tiles={3} />
      ) : (
        rows && (
          <div className="grid grid-cols-2 gap-3 md:gap-4 lg:grid-cols-3">
            <KpiTile label={t('budgets.total')} value={<Money value={totalBudget} />} />
            <KpiTile emphasis label={t('budgets.spent')} value={<CountUp value={totalSpent} format={(value) => formatMoney(value, locale)} />} />
            <KpiTile
              className="col-span-2 lg:col-span-1"
              label={left >= 0 ? t('budgets.left') : t('budgets.over')}
              value={<Money value={Math.abs(left)} className={left < 0 ? 'text-danger' : undefined} />}
            />
          </div>
        )
      )}

      <PageSection className="mt-6 md:mt-8" title={t('budgets.byDepartment')}>
        <ListPanel>
          {failed && !rows ? (
            <ErrorState onRetry={reload} />
          ) : !rows ? (
            <MeterRowsSkeleton rows={4} />
          ) : rows.length === 0 ? (
            <EmptyState
              icon={PiggyBank}
              title={t('budgets.empty')}
              description={t('budgets.emptyHint')}
              action={
                <Button asChild variant="secondary">
                  <Link to="/company/tickets?new=1">
                    <Icon icon={LifeBuoy} size={18} />
                    {t('budgets.ask')}
                  </Link>
                </Button>
              }
            />
          ) : (
            <ul className="divide-y divide-border">
              {rows.map((row) => {
                const name = row.department?.name ?? t('insights.unassigned');
                return (
                  <li key={row.key} className="flex flex-col gap-3 px-4 py-4 md:px-5">
                    <div className="flex flex-wrap items-start justify-between gap-x-4 gap-y-2">
                      <div className="min-w-0">
                        <p className="font-medium [overflow-wrap:anywhere]">{name}</p>
                        <p className="text-xs text-muted">
                          <Volume value={row.volumeMl} fractionDigits={0} />
                        </p>
                      </div>
                      <StatusChip tone={STATE_TONE[row.state]} label={t(`insights.budgetState.${row.state}`)} />
                    </div>
                    {row.budgetPiastres !== undefined && (
                      <UsageMeter percent={row.percent ?? 0} state={row.state} label={t('insights.budgetMeter', { name, percent: row.percent ?? 0 })} />
                    )}
                    <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 text-sm">
                      <span className="tabular-nums">
                        <span className="font-semibold">
                          <Money value={row.spentPiastres} />
                        </span>
                        {row.budgetPiastres !== undefined && <span className="text-muted"> {t('insights.ofAmount', { amount: formatMoney(row.budgetPiastres, locale) })}</span>}
                      </span>
                      {row.budgetPiastres !== undefined && (
                        <span className={row.state === 'over' ? 'font-medium text-danger' : 'text-muted'}>
                          {row.spentPiastres > row.budgetPiastres
                            ? t('insights.overBy', { amount: formatMoney(row.spentPiastres - row.budgetPiastres, locale) })
                            : t('insights.left', { amount: formatMoney(row.budgetPiastres - row.spentPiastres, locale) })}
                        </span>
                      )}
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
          {fuelings.data?.truncated && (
            <div className="border-t border-border px-4 py-3 md:px-5">
              <PartialNote count={fuelings.data.items.length} />
            </div>
          )}
        </ListPanel>
      </PageSection>
      <div className="mt-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <ViewOnlyNote />
        <Button asChild variant="ghost" size="sm">
          <Link to="/company/tickets?new=1">
            <Icon icon={LifeBuoy} size={16} />
            {t('budgets.ask')}
          </Link>
        </Button>
      </div>
    </>
  );
}
