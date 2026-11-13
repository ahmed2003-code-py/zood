import { Building, Fuel, Plus, Search } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { Button, DataView, Icon, ListRow, SegmentedControl, TextField, type DataColumn } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { ListPanel, PageHeader } from '@/shell/page';
import { opsApi, type Operator, type OperatorStatus, type Station, type StationStatus } from '../api';
import { OperatorStatusChip, StationStatusChip } from '../chips';
import { EmptyList, PagedList } from '../components';
import { useDebounced, usePagedResource } from '../use-resource';
import { OperatorFormSheet } from './operator-form';
import { StationFormSheet } from './station-form';

type View = 'stations' | 'operators';

/** Ops: the station network, with its operators one tab away. */
export function StationsPage() {
  const { t } = useTranslation('live');
  const navigate = useNavigate();
  const [view, setView] = useState<View>('stations');
  const [creating, setCreating] = useState(false);
  const stations = view === 'stations';

  return (
    <>
      <PageHeader
        title={t('stations.title')}
        description={t('stations.description')}
        actions={
          <Button leadingIcon={Plus} onClick={() => setCreating(true)}>
            {t(stations ? 'stations.new' : 'operators.new')}
          </Button>
        }
      />
      <div className="mb-4">
        <SegmentedControl<View>
          label={t('stations.view')}
          value={view}
          onValueChange={setView}
          options={[
            { value: 'stations', label: t('stations.tabStations') },
            { value: 'operators', label: t('stations.tabOperators') },
          ]}
        />
      </div>
      {stations ? <StationsList onCreate={() => setCreating(true)} /> : <OperatorsList onCreate={() => setCreating(true)} />}
      {creating && stations && (
        <StationFormSheet open onOpenChange={setCreating} onSaved={(station) => navigate(`/ops/stations/${station.id}`)} />
      )}
      {creating && !stations && (
        <OperatorFormSheet open onOpenChange={setCreating} onSaved={(operator) => navigate(`/ops/operators/${operator.id}`)} />
      )}
    </>
  );
}

type StationFilter = 'ALL' | StationStatus;

function StationsList({ onCreate }: { onCreate: () => void }) {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<StationFilter>('ALL');
  const query = useDebounced(search.trim());
  const stations = usePagedResource(
    (page, signal) => opsApi.stations.list({ ...page, q: query || undefined, status: status === 'ALL' ? undefined : status }, signal),
    [query, status],
  );
  const filtered = Boolean(query) || status !== 'ALL';
  const name = (station: Station) => (locale === 'ar' ? station.nameAr : station.nameEn);
  const fuels = (station: Station) => station.fuelTypes.map((fuel) => t(`enums.fuel.${fuel}`)).join(t('common.separator'));
  const open = (station: Station) => navigate(`/ops/stations/${station.id}`);

  const columns: DataColumn<Station>[] = [
    { id: 'station', header: t('stations.columns.station'), cell: (station) => <span className="font-medium">{name(station)}</span> },
    { id: 'timeZone', header: t('stations.columns.timeZone'), cell: (station) => <span dir="ltr">{station.timeZone}</span> },
    { id: 'connectivity', header: t('stations.columns.connectivity'), cell: (station) => t(`enums.connectivity.${station.connectivity}`) },
    { id: 'fuels', header: t('stations.columns.fuels'), cell: fuels },
    { id: 'status', header: t('stations.columns.status'), cell: (station) => <StationStatusChip status={station.status} /> },
  ];

  return (
    <>
      <div className="mb-4 flex flex-col gap-3 md:flex-row md:items-end">
        <TextField
          className="md:max-w-sm md:flex-1"
          label={t('stations.search')}
          leadingIcon={Search}
          type="search"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />
        <SegmentedControl<StationFilter>
          label={t('stations.statusFilter')}
          value={status}
          onValueChange={setStatus}
          options={[
            { value: 'ALL', label: t('common.all') },
            { value: 'ACTIVE', label: t('enums.stationStatus.ACTIVE') },
            { value: 'SUSPENDED', label: t('enums.stationStatus.SUSPENDED') },
            { value: 'CLOSED', label: t('enums.stationStatus.CLOSED') },
          ]}
        />
      </div>
      <ListPanel>
        <PagedList
          resource={stations}
          empty={
            filtered ? (
              <EmptyList title={t('stations.noResults')} description={t('stations.noResultsHint')} />
            ) : (
              <EmptyList
                title={t('stations.empty')}
                description={t('stations.emptyHint')}
                action={
                  <Button leadingIcon={Plus} onClick={onCreate}>
                    {t('stations.new')}
                  </Button>
                }
              />
            )
          }
        >
          {(items) => (
            <DataView
              caption={t('stations.title')}
              rows={items}
              columns={columns}
              getRowId={(station) => station.id}
              onRowClick={open}
              tableFrom="md"
              renderListItem={(station) => (
                <ListRow
                  leading={
                    <span className="flex size-9 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
                      <Icon icon={Fuel} size={18} />
                    </span>
                  }
                  title={name(station)}
                  subtitle={fuels(station)}
                  trailing={<StationStatusChip status={station.status} />}
                  onClick={() => open(station)}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
    </>
  );
}

type OperatorFilter = 'ALL' | OperatorStatus;

function OperatorsList({ onCreate }: { onCreate: () => void }) {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<OperatorFilter>('ALL');
  const query = useDebounced(search.trim());
  const operators = usePagedResource(
    (page, signal) => opsApi.operators.list({ ...page, q: query || undefined, status: status === 'ALL' ? undefined : status }, signal),
    [query, status],
  );
  const filtered = Boolean(query) || status !== 'ALL';
  const name = (operator: Operator) => (locale === 'ar' ? operator.nameAr : operator.nameEn);
  const open = (operator: Operator) => navigate(`/ops/operators/${operator.id}`);

  const columns: DataColumn<Operator>[] = [
    {
      id: 'operator',
      header: t('operators.columns.operator'),
      cell: (operator) => (
        <div className="min-w-0">
          <p className="font-medium">{name(operator)}</p>
          <p className="text-xs text-muted">{operator.legalName}</p>
        </div>
      ),
    },
    { id: 'taxId', header: t('operators.columns.taxId'), cell: (operator) => <span dir="ltr">{operator.taxId ?? t('common.notSet')}</span> },
    { id: 'status', header: t('operators.columns.status'), cell: (operator) => <OperatorStatusChip status={operator.status} /> },
  ];

  return (
    <>
      <div className="mb-4 flex flex-col gap-3 md:flex-row md:items-end">
        <TextField
          className="md:max-w-sm md:flex-1"
          label={t('operators.search')}
          leadingIcon={Search}
          type="search"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />
        <SegmentedControl<OperatorFilter>
          label={t('operators.statusFilter')}
          value={status}
          onValueChange={setStatus}
          options={[
            { value: 'ALL', label: t('common.all') },
            { value: 'ACTIVE', label: t('enums.operatorStatus.ACTIVE') },
            { value: 'SUSPENDED', label: t('enums.operatorStatus.SUSPENDED') },
          ]}
        />
      </div>
      <ListPanel>
        <PagedList
          resource={operators}
          empty={
            filtered ? (
              <EmptyList title={t('operators.noResults')} description={t('operators.noResultsHint')} />
            ) : (
              <EmptyList
                title={t('operators.empty')}
                description={t('operators.emptyHint')}
                action={
                  <Button leadingIcon={Plus} onClick={onCreate}>
                    {t('operators.new')}
                  </Button>
                }
              />
            )
          }
        >
          {(items) => (
            <DataView
              caption={t('operators.title')}
              rows={items}
              columns={columns}
              getRowId={(operator) => operator.id}
              onRowClick={open}
              tableFrom="md"
              renderListItem={(operator) => (
                <ListRow
                  leading={
                    <span className="flex size-9 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
                      <Icon icon={Building} size={18} />
                    </span>
                  }
                  title={name(operator)}
                  subtitle={operator.taxId ? <span dir="ltr">{operator.taxId}</span> : operator.legalName}
                  trailing={<OperatorStatusChip status={operator.status} />}
                  onClick={() => open(operator)}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
    </>
  );
}
