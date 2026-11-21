/**
 * Station portal: the fuelings at the operator's stations, newest first, filtered by station, status
 * and a validated date range (the calendar and a shift open it with ?from=&to=&stationId=). Export
 * writes the same filters to CSV. A row opens its detail in a sheet. Dates carry the year.
 */
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import { Download } from 'lucide-react';
import { apiDownload } from '@/api/http-client';
import { Banner, Button, DataView, EmptyState, ListRow, Money, Plate, SegmentedControl, Sheet, Volume, toast, type DataColumn } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cairoDay } from '@/lib/periods';
import { DetailList, ListPanel, PageHeader } from '@/shell/page';
import { stationApi, type Fueling, type FuelingStatus } from '../api';
import { FuelingStatusChip } from '../chips';
import { PagedList } from '../components';
import { cairoDayStart } from '../input';
import { usePagedResource, useResource } from '../use-resource';
import { useStationName, useValidRange, type DayRangeValue } from './hooks';
import { DayRangeFields, ListFilters, Stamp, StationSelect } from './parts';

type StatusFilter = 'ALL' | FuelingStatus;

function Amount({ fueling }: { fueling: Fueling }) {
  const { t } = useTranslation('live');
  if (fueling.status === 'DECLINED') return <span className="text-muted">{t('fuelings.dash')}</span>;
  if (!fueling.billable) return <span className="text-muted">{t('fuelings.notBilled')}</span>;
  return <Money value={fueling.amountPiastres} />;
}

export function StationFuelingsPage() {
  const { t } = useTranslation(['station', 'live']);
  const { locale } = useLocale();
  const [params] = useSearchParams();
  const [status, setStatus] = useState<StatusFilter>('ALL');
  const [stationId, setStationId] = useState(params.get('stationId') ?? '');
  const [range, setRange] = useState<DayRangeValue>({ from: params.get('from') ?? '', to: params.get('to') ?? '' });
  const [selected, setSelected] = useState<Fueling>();
  const [exporting, setExporting] = useState(false);
  const { valid, problem } = useValidRange(range);
  const fromAt = cairoDayStart(valid.from);
  const toAt = cairoDayStart(valid.to, 1);

  const stations = useResource((signal) => stationApi.stations({ limit: 100 }, signal), []);
  const stationName = useStationName(stations.data?.items);
  const fuelings = usePagedResource(
    (page, signal) => stationApi.fuelings({ ...page, status: status === 'ALL' ? undefined : status, from: fromAt, to: toAt, stationId: stationId || undefined }, signal),
    [status, fromAt, toAt, stationId],
  );
  const filters = (status !== 'ALL' ? 1 : 0) + (stationId ? 1 : 0) + (range.from || range.to ? 1 : 0);

  /** The filters on screen as CSV; this month so far when no range is chosen. */
  const exportCsv = async () => {
    if (problem) return;
    const today = cairoDay(new Date());
    const first = valid.from || `${today.slice(0, 8)}01`;
    const last = valid.to || today;
    const query = new URLSearchParams({ from: first, to: last, lang: locale });
    if (status !== 'ALL') query.set('status', status);
    if (stationId) query.set('stationId', stationId);
    setExporting(true);
    try {
      await apiDownload(`/station/fuelings/export?${query.toString()}`, `zawed-fuelings-${first}-${last}.csv`);
      toast(t('live.fuelings.exported'));
    } catch {
      toast(t('live:fuelings.exportFailed'), 'danger');
    } finally {
      setExporting(false);
    }
  };

  const columns: DataColumn<Fueling>[] = [
    { id: 'time', header: t('live:fuelings.time'), cell: (fueling) => <Stamp value={fueling.evaluatedAt} /> },
    { id: 'plate', header: t('live:vehicleForm.plate'), cell: (fueling) => <Plate value={fueling.plate} size="sm" /> },
    {
      id: 'company',
      header: t('live:fuelings.company'),
      cell: (fueling) => (
        <span className="flex flex-col">
          <span>{locale === 'ar' ? fueling.companyNameAr : fueling.companyNameEn}</span>
          <span className="text-xs text-muted">{locale === 'ar' ? fueling.stationNameAr : fueling.stationNameEn}</span>
        </span>
      ),
    },
    { id: 'fuel', header: t('live:vehicleForm.fuel'), cell: (fueling) => t(`live:enums.fuel.${fueling.fuelType}`) },
    { id: 'volume', header: t('live:fuelings.volume'), align: 'end', cell: (fueling) => <Volume value={fueling.volumeMl} /> },
    { id: 'amount', header: t('live:fuelings.amount'), align: 'end', cell: (fueling) => <Amount fueling={fueling} /> },
    { id: 'status', header: t('live.fuelings.status'), cell: (fueling) => <FuelingStatusChip status={fueling.status} declineReason={fueling.declineReason} origin={fueling.origin} conflicts={fueling.conflicts} /> },
  ];

  return (
    <>
      <PageHeader
        title={t('live.fuelings.title')}
        description={t('live.fuelings.description')}
        actions={
          <Button variant="secondary" leadingIcon={Download} loading={exporting} disabled={Boolean(problem)} onClick={() => void exportCsv()}>
            {t('live:fuelings.export')}
          </Button>
        }
      />
      <ListPanel>
        <ListFilters
          active={filters}
          onClear={() => {
            setStatus('ALL');
            setStationId('');
            setRange({ from: '', to: '' });
          }}
        >
          <SegmentedControl<StatusFilter>
            label={t('live:fuelings.statusFilter')}
            value={status}
            onValueChange={setStatus}
            options={[
              { value: 'ALL', label: t('live:common.all') },
              { value: 'COMPLETED', label: t('live:fuelings.status.COMPLETED') },
              { value: 'DECLINED', label: t('live:fuelings.status.DECLINED') },
            ]}
          />
          {(stations.data?.items.length ?? 0) > 1 && <StationSelect stations={stations.data?.items ?? []} value={stationId} onChange={setStationId} />}
          <DayRangeFields value={range} onChange={setRange} />
        </ListFilters>
        {problem && <Banner tone="warning" title={t(`live.range.problems.${problem}`)} className="m-4" />}
        <PagedList
          resource={fuelings}
          empty={
            filters > 0 ? (
              <EmptyState title={t('live:fuelings.noResults')} description={t('live:fuelings.noResultsHint')} />
            ) : (
              <EmptyState title={t('live:fuelings.empty')} description={t('live.fuelings.emptyHint')} />
            )
          }
        >
          {(items) => (
            <DataView
              caption={t('live.fuelings.title')}
              rows={items}
              columns={columns}
              getRowId={(fueling) => fueling.id}
              onRowClick={setSelected}
              tableFrom="lg"
              renderListItem={(fueling) => (
                <ListRow
                  title={<Plate value={fueling.plate} size="sm" />}
                  subtitle={
                    <>
                      <Stamp value={fueling.evaluatedAt} /> · {t(`live:enums.fuel.${fueling.fuelType}`)} · <Volume value={fueling.volumeMl} />
                    </>
                  }
                  trailing={<Amount fueling={fueling} />}
                  trailingSub={<FuelingStatusChip status={fueling.status} declineReason={fueling.declineReason} origin={fueling.origin} conflicts={fueling.conflicts} />}
                  onClick={() => setSelected(fueling)}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
      <Sheet
        open={Boolean(selected)}
        onOpenChange={(open) => !open && setSelected(undefined)}
        title={t('live:fuelings.detailTitle', { reference: selected?.reference ?? '' })}
      >
        {selected && <StationFuelingDetail fueling={selected} station={stationName(selected.stationId)} />}
      </Sheet>
    </>
  );
}

/** One fueling as the station sees it: no vehicle record, the company's name, dates with the year. */
function StationFuelingDetail({ fueling, station }: { fueling: Fueling; station?: string }) {
  const { t } = useTranslation(['station', 'live']);
  const { locale } = useLocale();
  return (
    <div className="flex flex-col gap-4">
      {fueling.conflicts.length > 0 && (
        <Banner tone="warning" title={t('live:fuelings.conflictsTitle')} description={fueling.conflicts.map((code) => t(`live:fuelings.conflict.${code}`)).join(' · ')} />
      )}
      {fueling.status === 'DECLINED' && fueling.declineReason && (
        <Banner tone="danger" title={t(`live:fuelings.decline.${fueling.declineReason}`)} description={t(`live:fuelings.declineHint.${fueling.declineReason}`)} />
      )}
      <ListPanel>
        <div className="flex justify-center border-b border-border p-5">
          <Plate value={fueling.plate} size="lg" />
        </div>
        <DetailList
          rows={[
            { label: t('live:fuelings.reference'), value: <bdi dir="ltr">{fueling.reference}</bdi> },
            { label: t('live.fuelings.status'), value: <FuelingStatusChip status={fueling.status} declineReason={fueling.declineReason} origin={fueling.origin} conflicts={fueling.conflicts} /> },
            { label: t('live:fuelings.company'), value: locale === 'ar' ? fueling.companyNameAr : fueling.companyNameEn },
            station ? { label: t('live.filters.station'), value: station } : false,
            { label: t('live:vehicleForm.fuel'), value: t(`live:enums.fuel.${fueling.fuelType}`) },
            { label: t('live:fuelings.volume'), value: <Volume value={fueling.volumeMl} /> },
            { label: t('live:fuelings.unitPrice'), value: <Money value={fueling.unitPricePiastres} /> },
            { label: t('live:fuelings.amount'), value: <Amount fueling={fueling} /> },
            { label: t('live:fuelings.agent'), value: <bdi dir="ltr">{fueling.agentWorkerId}</bdi> },
            { label: t('live:fuelings.origin'), value: t(`live:fuelings.originValue.${fueling.origin}`) },
            { label: t('live:fuelings.time'), value: <Stamp value={fueling.evaluatedAt} /> },
            { label: t('live:fuelings.deviceAt'), value: <Stamp value={fueling.deviceAt} /> },
          ]}
        />
      </ListPanel>
      {!fueling.billable && fueling.status === 'COMPLETED' && <Banner tone="info" title={t('live:fuelings.notBilled')} description={t('live:fuelings.notBilledHint')} />}
    </div>
  );
}
