import { Gauge, LifeBuoy, Search } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, EmptyState, ErrorState, FilterBar, Icon, Plate, SegmentedControl, TextField } from '@/design-system';
import { cairoDay, periodRange } from '@/lib/periods';
import { ListPanel, PageHeader, ViewOnlyNote } from '@/shell/page';
import { companyApi } from '../api';
import { LimitUsageRow } from '../insights/LimitUsageRow';
import { collectPages, fuelingsOf, limitUsage, mapWithConcurrency } from '../insights/model';
import { MeterRowsSkeleton, PartialNote } from '../insights/ui';
import { loadFuelings } from '../insights/use-company-data';
import { useResource } from '../use-resource';

/** The first vehicles cover a pilot fleet; each one's limits are a separate request. */
const VEHICLE_LIMIT = 200;

type Filter = 'ALL' | 'ATTENTION';


/** Company portal: every vehicle's limits and what each has used in its current day, week or month. */
export function CompanyLimitsPage() {
  const { t } = useTranslation(['portal', 'live']);
  const [filter, setFilter] = useState<Filter>('ALL');
  const [search, setSearch] = useState('');
  const now = useMemo(() => new Date(), []);
  // The current week can start in the previous month, so the window opens at whichever is earlier.
  const window = useMemo(() => {
    const week = periodRange('THIS_WEEK', now);
    const month = periodRange('THIS_MONTH', now);
    return { from: week.from < month.from ? week.from : month.from, to: cairoDay(now) };
  }, [now]);

  const data = useResource(async (signal) => {
    const vehicles = await collectPages((page, s) => companyApi.vehicles({ ...page, status: 'ACTIVE' }, s), { max: VEHICLE_LIMIT, signal });
    const [details, fuelings] = await Promise.all([
      mapWithConcurrency(vehicles.items, 6, (vehicle) => companyApi.vehicle(vehicle.id, signal)),
      loadFuelings(window, signal),
    ]);
    return { details, fuelings, truncated: vehicles.truncated };
  }, [window.from]);

  const rows = useMemo(() => {
    if (!data.data) return undefined;
    return data.data.details.map(({ vehicle, limits }) => ({
      vehicle,
      usage: limitUsage(limits, fuelingsOf(data.data!.fuelings.items, vehicle.id), now),
    }));
  }, [data.data, now]);

  const query = search.trim();
  const visible = rows
    ?.filter((row) => row.usage.length > 0)
    .filter((row) => filter === 'ALL' || row.usage.some((usage) => usage.state !== 'ok'))
    .filter((row) => !query || row.vehicle.plate.includes(query) || (row.vehicle.fleetCode ?? '').toLowerCase().includes(query.toLowerCase()))
    .sort((a, b) => Math.max(...b.usage.map((u) => u.percent)) - Math.max(...a.usage.map((u) => u.percent)));
  const withoutLimits = rows?.filter((row) => row.usage.length === 0).length ?? 0;
  const attention = rows?.filter((row) => row.usage.some((usage) => usage.state !== 'ok')).length ?? 0;

  return (
    <>
      <PageHeader title={t('limits.title')} description={t('limits.description')} />
      <ListPanel>
        <FilterBar>
          <TextField
            className="md:max-w-xs md:flex-1"
            label={t('limits.search')}
            leadingIcon={Search}
            type="search"
            data-page-search
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
          <SegmentedControl<Filter>
            label={t('limits.filter')}
            value={filter}
            onValueChange={setFilter}
            options={[
              { value: 'ALL', label: t('live:common.all') },
              { value: 'ATTENTION', label: t('limits.attention', { count: attention }) },
            ]}
          />
        </FilterBar>
        {data.error && !data.data ? (
          <ErrorState onRetry={data.reload} />
        ) : !visible ? (
          <MeterRowsSkeleton rows={4} />
        ) : visible.length === 0 ? (
          <EmptyState
            icon={Gauge}
            title={filter === 'ATTENTION' ? t('limits.allClear') : query ? t('limits.noMatch') : t('limits.empty')}
            description={filter === 'ATTENTION' ? t('limits.allClearHint') : query ? t('limits.noMatchHint') : t('limits.emptyHint')}
          />
        ) : (
          <ul className="divide-y divide-border">
            {visible.map(({ vehicle, usage }) => (
              <li key={vehicle.id} className="@container px-4 py-4 md:px-5">
                <div className="flex flex-col gap-4 @2xl:flex-row @2xl:gap-8">
                  <Link to={`/company/vehicles/${vehicle.id}`} className="flex min-w-0 shrink-0 flex-col gap-1 rounded-control @2xl:w-56">
                    <Plate value={vehicle.plate} size="sm" className="self-start" />
                    <span className="truncate text-sm text-muted">
                      {[vehicle.fleetCode, `${vehicle.make} ${vehicle.model}`].filter(Boolean).join(' · ')}
                    </span>
                  </Link>
                  <div className="grid min-w-0 flex-1 grid-cols-1 gap-4 @xl:grid-cols-2">
                    {usage.map((item) => (
                      <LimitUsageRow key={item.limit.id} usage={item} />
                    ))}
                  </div>
                </div>
              </li>
            ))}
          </ul>
        )}
        {(withoutLimits > 0 || data.data?.truncated || data.data?.fuelings.truncated) && (
          <div className="flex flex-col gap-2 border-t border-border px-4 py-3 md:px-5">
            {withoutLimits > 0 && <p className="text-xs text-muted">{t('limits.withoutLimits', { count: withoutLimits })}</p>}
            {data.data?.fuelings.truncated && <PartialNote count={data.data.fuelings.items.length} />}
            {data.data?.truncated && <p className="text-xs text-muted">{t('limits.firstVehicles', { count: VEHICLE_LIMIT })}</p>}
          </div>
        )}
      </ListPanel>
      <div className="mt-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <ViewOnlyNote />
        <Button asChild variant="ghost" size="sm">
          <Link to="/company/tickets?new=1&type=LIMIT_CHANGE">
            <Icon icon={LifeBuoy} size={16} />
            {t('limits.ask')}
          </Link>
        </Button>
      </div>
    </>
  );
}
