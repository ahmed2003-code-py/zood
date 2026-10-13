/** Company: transactions, transaction details, vehicles and vehicle details (view-only, PRD §6.2). */
import { MessageSquareText } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router';
import type { Vehicle } from '@/domain/types';
import {
  Button,
  DataView,
  DateTimeText,
  EmptyState,
  Icon,
  KpiTile,
  Money,
  Plate,
  StatusChip,
  TierChip,
  Volume,
  type DataColumn,
} from '@/design-system';
import { DEFAULT_FILTERS, type FilterValues } from '@/features/transactions/filters';
import { TransactionFilters } from '@/features/transactions/TransactionFilters';
import { TransactionDetail } from '@/features/transactions/TransactionDetail';
import { TransactionList } from '@/features/transactions/TransactionList';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatMoney, formatNumber, formatRelative, formatVolume } from '@/lib/format';
import { localize } from '@/lib/locale';
import { useNow } from '@/lib/use-now';
import { DEMO_CONTEXT } from '@/mocks/personas';
import { filterTransactions, lastFueling, vehicleUsage } from '@/mocks/selectors';
import { useDemoStore } from '@/mocks/store';
import { BackLink, DetailList, ListPanel, PageHeader, PageSection, ViewOnlyNote } from '@/shell/page';

const companyId = DEMO_CONTEXT.companyId;
const PAGE_SIZE = 25;

export function CompanyTransactions() {
  const { t } = useTranslation(['company', 'navigation', 'transactions', 'errors']);
  const { locale } = useLocale();
  const navigate = useNavigate();
  const data = useDemoStore((state) => state.data);
  const now = useNow();
  const [filters, setFilters] = useState<FilterValues>(DEFAULT_FILTERS);
  const [limit, setLimit] = useState(PAGE_SIZE);
  const rows = useMemo(
    () =>
      filterTransactions(
        data,
        { companyId, period: filters.period, status: filters.status, vehicleId: filters.vehicleId, filterStationId: filters.stationId },
        now,
      ),
    [data, filters, now],
  );
  const vehicles = useMemo(
    () =>
      Object.values(data.vehicles)
        .filter((vehicle) => vehicle.companyId === companyId)
        .map((vehicle) => ({ value: vehicle.id, label: vehicle.plate })),
    [data.vehicles],
  );
  const stations = Object.values(data.stations).map((station) => ({ value: station.id, label: localize(station.name, locale) }));

  return (
    <>
      <PageHeader title={t('navigation:items.transactions')} description={t('transactions.description')} />
      <TransactionFilters
        value={filters}
        onChange={(next) => {
          setFilters(next);
          setLimit(PAGE_SIZE);
        }}
        vehicles={vehicles}
        stations={stations}
      />
      <ListPanel>
        {rows.length === 0 ? (
          <EmptyState title={t('errors:noResults.title')} description={t('errors:noResults.description')} />
        ) : (
          <TransactionList transactions={rows.slice(0, limit)} perspective="company" onOpen={(txn) => navigate(`/company/transactions/${txn.id}`)} />
        )}
      </ListPanel>
      {rows.length > limit && (
        <div className="mt-4 flex justify-center">
          <Button variant="secondary" onClick={() => setLimit((value) => value + PAGE_SIZE)}>
            {t('transactions:showMore')}
          </Button>
        </div>
      )}
    </>
  );
}

export function CompanyTransactionDetail() {
  const { t } = useTranslation('transactions');
  const { id } = useParams();
  const transaction = useDemoStore((state) => state.data.transactions.find((txn) => txn.id === id && txn.companyId === companyId));
  return (
    <>
      <BackLink to="/company/transactions" label={t('detail.back')} />
      <PageHeader title={t('detail.title')} />
      {transaction ? <TransactionDetail transaction={transaction} /> : <EmptyState title={t('detail.notFound')} description="" />}
    </>
  );
}

export function CompanyVehicles() {
  const { t } = useTranslation(['company', 'navigation', 'status', 'transactions']);
  const { locale } = useLocale();
  const navigate = useNavigate();
  const data = useDemoStore((state) => state.data);
  const now = useNow();
  const vehicles = useMemo(() => Object.values(data.vehicles).filter((vehicle) => vehicle.companyId === companyId), [data.vehicles]);
  const active = vehicles.filter((vehicle) => vehicle.status === 'active').length;
  const name = (vehicle: Vehicle) => `${localize(vehicle.make, locale)} ${localize(vehicle.model, locale)} ${vehicle.year}`;
  const last = (vehicle: Vehicle) => {
    const txn = lastFueling(data, vehicle.id);
    return txn ? formatRelative(txn.deviceAt, locale, now) : t('vehicles.noFueling');
  };
  const statusChip = (vehicle: Vehicle) =>
    vehicle.status !== 'active' ? (
      <StatusChip tone="warning" label={t(`status:vehicle.${vehicle.status}`)} />
    ) : vehicle.tier === 'vip' ? (
      <TierChip tier="vip" />
    ) : (
      <StatusChip tone="success" label={t('status:vehicle.active')} />
    );
  const columns: DataColumn<Vehicle>[] = [
    { id: 'plate', header: t('transactions:columns.vehicle'), cell: (vehicle) => <Plate value={vehicle.plate} size="sm" /> },
    { id: 'model', header: t('vehicleDetail.identity'), cell: name },
    { id: 'fuel', header: t('vehicleDetail.fuel'), cell: (vehicle) => localize(data.fuelTypes[vehicle.fuelTypeId]?.name, locale) },
    { id: 'department', header: t('vehicleDetail.department'), cell: (vehicle) => localize(data.departments[vehicle.departmentId]?.name, locale) },
    { id: 'last', header: t('vehicleDetail.lastFueling'), cell: (vehicle) => <span className="text-muted">{last(vehicle)}</span> },
    { id: 'status', header: t('vehicleDetail.status'), cell: statusChip },
  ];
  return (
    <>
      <PageHeader title={t('navigation:items.vehicles')} description={t('vehicles.description')} />
      <dl className="mb-6 flex flex-wrap gap-x-8 gap-y-3">
        {[
          [t('vehicles.stats.active'), active],
          [t('vehicles.stats.suspended'), vehicles.filter((vehicle) => vehicle.status === 'suspended').length],
          [t('vehicles.stats.vip'), vehicles.filter((vehicle) => vehicle.tier === 'vip').length],
        ].map(([label, value]) => (
          <div key={String(label)}>
            <dt className="text-sm text-muted">{label}</dt>
            <dd className="text-2xl font-semibold tabular-nums">{formatNumber(Number(value), locale)}</dd>
          </div>
        ))}
      </dl>
      <ListPanel>
        <DataView
          caption={t('navigation:items.vehicles')}
          rows={vehicles}
          columns={columns}
          getRowId={(vehicle) => vehicle.id}
          onRowClick={(vehicle) => navigate(`/company/vehicles/${vehicle.id}`)}
          renderListItem={(vehicle) => (
            <button
              type="button"
              onClick={() => navigate(`/company/vehicles/${vehicle.id}`)}
              className="flex min-h-16 w-full items-center gap-3 px-4 py-3 text-start transition-colors hover:bg-surface-sunken"
            >
              <Plate value={vehicle.plate} size="sm" />
              <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="truncate font-medium">{name(vehicle)}</span>
                <span className="truncate text-sm text-muted">{last(vehicle)}</span>
              </span>
              {statusChip(vehicle)}
            </button>
          )}
        />
      </ListPanel>
      <div className="mt-6">
        <ViewOnlyNote />
      </div>
    </>
  );
}

export function CompanyVehicleDetail() {
  const { t } = useTranslation(['company', 'status', 'common']);
  const { locale } = useLocale();
  const navigate = useNavigate();
  const { id = '' } = useParams();
  const data = useDemoStore((state) => state.data);
  const now = useNow();
  const vehicle = data.vehicles[id];
  const usage = useMemo(() => vehicleUsage(data, id, now), [data, id, now]);
  if (!vehicle || vehicle.companyId !== companyId) {
    return (
      <>
        <BackLink to="/company/vehicles" label={t('vehicleDetail.back')} />
        <EmptyState title={t('vehicleDetail.notFound')} description="" />
      </>
    );
  }
  const tag = data.tags[vehicle.tagId];
  const last = lastFueling(data, vehicle.id);
  const rule = usage.rule;
  const ruleValue = (value: number) =>
    rule?.kind === 'amount' ? formatMoney(value, locale, { precision: 'whole' }) : formatVolume(value, locale, 0);
  const ratio = rule ? Math.min(1, usage.usedInPeriod / rule.maxValue) : 0;

  return (
    <>
      <BackLink to="/company/vehicles" label={t('vehicleDetail.back')} />
      <div className="mb-6 flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
        <div className="flex flex-col items-start gap-3">
          <Plate value={vehicle.plate} size="lg" />
          <h1 className="text-xl font-semibold md:text-2xl">
            {localize(vehicle.make, locale)} {localize(vehicle.model, locale)} {vehicle.year}
          </h1>
          <div className="flex flex-wrap gap-2">
            <StatusChip tone={vehicle.status === 'active' ? 'success' : 'warning'} label={t(`status:vehicle.${vehicle.status}`)} />
            <TierChip tier={vehicle.tier} />
          </div>
        </div>
        <Button asChild variant="secondary">
          <Link to="/company/requests">
            <Icon icon={MessageSquareText} size={18} />
            {t('vehicleDetail.requestChange')}
          </Link>
        </Button>
      </div>

      <div className="grid grid-cols-1 gap-8 lg:grid-cols-2">
        <div className="flex min-w-0 flex-col gap-6">
          <PageSection title={t('vehicleDetail.limit')}>
            <div className="rounded-container border border-border bg-surface p-4">
              {rule && vehicle.tier === 'normal' ? (
                <>
                  <p className="font-semibold">{t(`limits.rule.${rule.kind}_${rule.period}`, { value: ruleValue(rule.maxValue) })}</p>
                  <div className="mt-3 h-2 overflow-hidden rounded-full bg-surface-sunken">
                    <div className={cn('h-full rounded-full', ratio >= 1 ? 'bg-danger' : ratio >= 0.8 ? 'bg-warning' : 'bg-primary')} style={{ width: `${Math.round(ratio * 100)}%` }} />
                  </div>
                  <p className="mt-2 text-sm text-muted">
                    {t('vehicleDetail.usedToday', { used: ruleValue(usage.usedInPeriod), max: ruleValue(rule.maxValue) })}
                  </p>
                </>
              ) : (
                <p className="text-sm text-muted">{t('vehicleDetail.noLimit')}</p>
              )}
            </div>
          </PageSection>
          <ListPanel>
            <DetailList
              rows={[
                { label: t('vehicleDetail.fuel'), value: localize(data.fuelTypes[vehicle.fuelTypeId]?.name, locale) },
                { label: t('vehicleDetail.tank'), value: formatVolume(vehicle.tankCapacityMl, locale, 0) },
                { label: t('vehicleDetail.department'), value: localize(data.departments[vehicle.departmentId]?.name, locale) },
                {
                  label: t('vehicleDetail.tag'),
                  value: tag ? <StatusChip tone={tag.status === 'active' ? 'success' : 'danger'} label={t(`status:tag.${tag.status}`)} /> : '—',
                },
                { label: t('vehicleDetail.lastFueling'), value: last ? <DateTimeText value={last.deviceAt} /> : t('vehicleDetail.never') },
              ]}
            />
          </ListPanel>
        </div>
        <div className="flex min-w-0 flex-col gap-6">
          <div className="grid grid-cols-2 gap-3">
            <KpiTile label={t('vehicleDetail.monthVolume')} value={<Volume value={usage.monthVolumeMl} fractionDigits={0} />} />
            <KpiTile label={t('vehicleDetail.monthSpend')} value={<Money value={usage.monthSpendPiastres} precision="whole" />} />
          </div>
          <PageSection title={t('vehicleDetail.recent')}>
            <ListPanel>
              <TransactionList
                transactions={usage.recent}
                perspective="company"
                compact
                onOpen={(txn) => navigate(`/company/transactions/${txn.id}`)}
              />
            </ListPanel>
          </PageSection>
        </div>
      </div>
      <div className="mt-6">
        <ViewOnlyNote />
      </div>
    </>
  );
}
