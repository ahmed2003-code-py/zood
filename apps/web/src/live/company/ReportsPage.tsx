import { ChartColumn, Download, FileSpreadsheet } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { apiDownload } from '@/api/http-client';
import {
  Button,
  CountUp,
  DataView,
  EmptyState,
  ErrorState,
  FilterBar,
  KpiTile,
  ListRow,
  Money,
  Plate,
  SegmentedControl,
  toast,
  Volume,
  type DataColumn,
} from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatMoney, formatNumber, formatVolume } from '@/lib/format';
import { cairoDay } from '@/lib/periods';
import { ListPanel, PageHeader, PageSection } from '@/shell/page';
import type { Department, Vehicle } from '../api';
import { DateRangeFields } from '../date-range';
import { useDateRange, usableRange } from '../date-range-state';
import { downloadCsv, litresCell, NO_DEPARTMENT, piastresCell, spendByDepartment, spendByFuel, spendByVehicle, toCsv, type SpendRow } from '../insights/model';
import { KpiRowSkeleton, PartialNote, UsageMeter } from '../insights/ui';
import { useCompanyDepartments, useCompanyFuelings, useCompanyVehicles } from '../insights/use-company-data';

type Group = 'department' | 'vehicle' | 'fuel';

/** Company portal: where the fuel money went over a period, by department, vehicle or fuel, with CSV exports. */
export function CompanyReportsPage() {
  const { t } = useTranslation(['portal', 'live']);
  const { locale } = useLocale();
  const navigate = useNavigate();
  const range = useDateRange('THIS_MONTH');
  const usable = usableRange(range.from, range.to);
  const today = cairoDay(new Date());
  const period = usable.valid ? { from: usable.from || `${today.slice(0, 8)}01`, to: usable.to || today } : undefined;
  const [group, setGroup] = useState<Group>('department');
  const [exporting, setExporting] = useState(false);
  const fuelings = useCompanyFuelings(period);
  const vehicles = useCompanyVehicles();
  const departments = useCompanyDepartments();

  const vehicleById = useMemo(() => new Map((vehicles.data?.items ?? []).map((vehicle) => [vehicle.id, vehicle])), [vehicles.data]);
  const departmentById = useMemo(() => new Map((departments.data?.items ?? []).map((department) => [department.id, department])), [departments.data]);
  const rows = useMemo(() => {
    if (!fuelings.data) return undefined;
    if (group === 'vehicle') return spendByVehicle(fuelings.data.items);
    if (group === 'fuel') return spendByFuel(fuelings.data.items);
    return vehicles.data ? spendByDepartment(fuelings.data.items, vehicles.data.items) : undefined;
  }, [fuelings.data, vehicles.data, group]);

  const total = rows?.reduce((sum, row) => ({ amount: sum.amount + row.amountPiastres, volume: sum.volume + row.volumeMl, count: sum.count + row.fuelings }), { amount: 0, volume: 0, count: 0 });
  const nameOf = (row: SpendRow): string => {
    if (group === 'fuel') return t(`live:enums.fuel.${row.key}` as 'live:enums.fuel.DIESEL');
    if (group === 'department') return row.key === NO_DEPARTMENT ? t('insights.unassigned') : (departmentById.get(row.key) as Department | undefined)?.name ?? t('insights.unknownDepartment');
    const vehicle = vehicleById.get(row.key) as Vehicle | undefined;
    return vehicle ? vehicle.plate : t('insights.vehicle');
  };
  const share = (row: SpendRow) => (total && total.amount > 0 ? Math.round((row.amountPiastres * 100) / total.amount) : 0);

  const label = (row: SpendRow) =>
    group === 'vehicle' && vehicleById.get(row.key) ? (
      <span className="flex items-center gap-2">
        <Plate value={vehicleById.get(row.key)!.plate} size="sm" />
        <span className="hidden truncate text-xs text-muted xl:inline">{`${vehicleById.get(row.key)!.make} ${vehicleById.get(row.key)!.model}`}</span>
      </span>
    ) : (
      <span className="font-medium [overflow-wrap:anywhere]">{nameOf(row)}</span>
    );

  const columns: DataColumn<SpendRow>[] = [
    { id: 'name', header: t(`reports.groups.${group}`), cell: label },
    { id: 'fuelings', header: t('reports.columns.fuelings'), align: 'end', cell: (row) => formatNumber(row.fuelings, locale) },
    { id: 'litres', header: t('reports.columns.litres'), align: 'end', cell: (row) => <Volume value={row.volumeMl} /> },
    { id: 'amount', header: t('reports.columns.amount'), align: 'end', cell: (row) => <Money value={row.amountPiastres} /> },
    {
      id: 'share',
      header: t('reports.columns.share'),
      className: 'w-40',
      cell: (row) => (
        <span className="flex items-center gap-2">
          <UsageMeter percent={share(row)} state="ok" label={t('insights.shareMeter', { percent: share(row) })} className="h-1.5" />
          <span className="w-9 shrink-0 text-end text-xs text-muted tabular-nums">{`${share(row)}%`}</span>
        </span>
      ),
    },
  ];

  const exportBreakdown = () => {
    if (!rows || !period) return;
    const csv = toCsv([
      [t(`reports.groups.${group}`), t('reports.columns.fuelings'), t('reports.csv.litres'), t('reports.csv.amount'), t('reports.columns.share')],
      ...rows.map((row) => [nameOf(row), row.fuelings, litresCell(row.volumeMl), piastresCell(row.amountPiastres), `${share(row)}%`]),
    ]);
    downloadCsv(csv, `zawed-spend-by-${group}-${period.from}-${period.to}.csv`);
    toast(t('reports.exported'));
  };

  const exportFuelings = async () => {
    if (!period) return;
    setExporting(true);
    try {
      const query = new URLSearchParams({ from: period.from, to: period.to, lang: locale });
      await apiDownload(`/company/fuelings/export?${query.toString()}`, `zawed-fuelings-${period.from}-${period.to}.csv`);
    } catch {
      toast(t('live:fuelings.exportFailed'), 'danger');
    } finally {
      setExporting(false);
    }
  };

  const failed = Boolean(fuelings.error || (group === 'department' && vehicles.error));

  return (
    <>
      <PageHeader
        title={t('reports.title')}
        description={t('reports.description')}
        actions={
          <>
            <Button variant="secondary" leadingIcon={FileSpreadsheet} disabled={!rows || rows.length === 0} onClick={exportBreakdown}>
              {t('reports.exportBreakdown')}
            </Button>
            <Button variant="secondary" leadingIcon={Download} loading={exporting} disabled={!period} onClick={() => void exportFuelings()}>
              {t('reports.exportFuelings')}
            </Button>
          </>
        }
      />
      <ListPanel>
        <FilterBar>
          <DateRangeFields range={range} />
        </FilterBar>
        <div className="p-4 md:p-5">
          {!total && !failed ? (
            <KpiRowSkeleton tiles={3} />
          ) : (
            total && (
              <div className="grid grid-cols-2 gap-3 md:gap-4 lg:grid-cols-3">
                <KpiTile emphasis label={t('reports.totals.amount')} value={<CountUp value={total.amount} format={(value) => formatMoney(value, locale)} />} />
                <KpiTile label={t('reports.totals.litres')} value={<CountUp value={total.volume} format={(value) => formatVolume(value, locale, 0)} />} />
                <KpiTile className="col-span-2 lg:col-span-1" label={t('reports.totals.fuelings')} value={formatNumber(total.count, locale)} />
              </div>
            )
          )}
        </div>
      </ListPanel>

      <PageSection
        className="mt-6 md:mt-8"
        title={t('reports.breakdown')}
        action={
          <SegmentedControl<Group>
            label={t('reports.groupBy')}
            value={group}
            onValueChange={setGroup}
            options={[
              { value: 'department', label: t('reports.groups.department') },
              { value: 'vehicle', label: t('reports.groups.vehicle') },
              { value: 'fuel', label: t('reports.groups.fuel') },
            ]}
          />
        }
      >
        <ListPanel>
          {failed && !rows ? (
            <ErrorState onRetry={() => (fuelings.reload(), vehicles.reload())} />
          ) : (
            <DataView
              caption={t('reports.breakdown')}
              loading={!rows}
              rows={rows ?? []}
              columns={columns}
              getRowId={(row) => row.key || 'none'}
              tableFrom="md"
              onRowClick={group === 'vehicle' ? (row) => navigate(`/company/vehicles/${row.key}`) : undefined}
              empty={<EmptyState icon={ChartColumn} title={t('reports.empty')} description={t('reports.emptyHint')} />}
              renderListItem={(row) => (
                <ListRow
                  title={label(row)}
                  subtitle={`${formatNumber(row.fuelings, locale)} · ${formatVolume(row.volumeMl, locale)} · ${share(row)}%`}
                  trailing={<Money value={row.amountPiastres} />}
                  onClick={group === 'vehicle' ? () => navigate(`/company/vehicles/${row.key}`) : undefined}
                />
              )}
            />
          )}
          {fuelings.data?.truncated && (
            <div className="border-t border-border px-4 py-3 md:px-5">
              <PartialNote count={fuelings.data.items.length} />
            </div>
          )}
        </ListPanel>
      </PageSection>
    </>
  );
}
