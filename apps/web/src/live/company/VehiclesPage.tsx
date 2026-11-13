import { Search } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { DataView, ListRow, Plate, SegmentedControl, TextField, type DataColumn } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatVolume } from '@/lib/format';
import { ListPanel, PageHeader, ViewOnlyNote } from '@/shell/page';
import { companyApi, type Vehicle, type VehicleStatus } from '../api';
import { VehicleStatusChip } from '../chips';
import { EmptyList, PagedList } from '../components';
import { useDebounced, usePagedResource } from '../use-resource';

type StatusFilter = 'ALL' | VehicleStatus;

/** Company portal: the company's own vehicles, read only. */
export function CompanyVehiclesPage() {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<StatusFilter>('ALL');
  const query = useDebounced(search.trim());
  const vehicles = usePagedResource(
    (page, signal) => companyApi.vehicles({ ...page, q: query || undefined, status: status === 'ALL' ? undefined : status }, signal),
    [query, status],
  );
  const filtered = Boolean(query) || status !== 'ALL';
  const open = (vehicle: Vehicle) => navigate(`/company/vehicles/${vehicle.id}`);

  const columns: DataColumn<Vehicle>[] = [
    { id: 'plate', header: t('vehicleForm.plate'), cell: (vehicle) => <Plate value={vehicle.plate} size="sm" /> },
    { id: 'model', header: t('vehicleForm.model'), cell: (vehicle) => `${vehicle.make} ${vehicle.model}` },
    { id: 'fuel', header: t('vehicleForm.fuel'), cell: (vehicle) => t(`enums.fuel.${vehicle.fuelType}`) },
    { id: 'tank', header: t('vehicle.tank'), align: 'end', cell: (vehicle) => formatVolume(vehicle.tankCapacityMl, locale, 0) },
    { id: 'tier', header: t('vehicleForm.tier'), cell: (vehicle) => t(`enums.tier.${vehicle.tier}`) },
    { id: 'status', header: t('vehicle.status'), cell: (vehicle) => <VehicleStatusChip status={vehicle.status} /> },
  ];

  return (
    <>
      <PageHeader title={t('portal.vehicles.title')} description={t('portal.vehicles.description')} />
      <div className="mb-4 flex flex-col gap-3 md:flex-row md:items-end">
        <TextField
          className="md:max-w-sm md:flex-1"
          label={t('portal.vehicles.search')}
          leadingIcon={Search}
          type="search"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />
        <SegmentedControl<StatusFilter>
          label={t('portal.vehicles.statusFilter')}
          value={status}
          onValueChange={setStatus}
          options={[
            { value: 'ALL', label: t('common.all') },
            { value: 'ACTIVE', label: t('enums.vehicleStatus.ACTIVE') },
            { value: 'SUSPENDED', label: t('enums.vehicleStatus.SUSPENDED') },
            { value: 'DECOMMISSIONED', label: t('enums.vehicleStatus.DECOMMISSIONED') },
          ]}
        />
      </div>
      <ListPanel>
        <PagedList
          resource={vehicles}
          empty={
            filtered ? (
              <EmptyList title={t('portal.vehicles.noResults')} description={t('portal.vehicles.noResultsHint')} />
            ) : (
              <EmptyList title={t('portal.vehicles.empty')} description={t('portal.vehicles.emptyHint')} />
            )
          }
        >
          {(items) => (
            <DataView
              caption={t('portal.vehicles.title')}
              rows={items}
              columns={columns}
              getRowId={(vehicle) => vehicle.id}
              onRowClick={open}
              tableFrom="md"
              renderListItem={(vehicle) => (
                <ListRow
                  title={<Plate value={vehicle.plate} size="sm" />}
                  subtitle={`${vehicle.make} ${vehicle.model} · ${t(`enums.fuel.${vehicle.fuelType}`)}`}
                  trailing={<VehicleStatusChip status={vehicle.status} />}
                  onClick={() => open(vehicle)}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
      <div className="mt-4">
        <ViewOnlyNote />
      </div>
    </>
  );
}
