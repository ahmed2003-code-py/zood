/**
 * Fuelings on live data, shared by the three portals: the list with its filters, and the detail.
 * Each portal passes its own loader, so what a caller may see is decided by the API, never here.
 */
import { Link, useParams, useSearchParams } from 'react-router';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Banner,
  Button,
  DataView,
  DateTimeText,
  ErrorState,
  ListRow,
  Money,
  Plate,
  SegmentedControl,
  Skeleton,
  Volume,
  toast,
  FilterBar,
  type DataColumn,
} from '@/design-system';
import { Download } from 'lucide-react';
import { apiDownload } from '@/api/http-client';
import { useLocale } from '@/i18n/useLocale';
import { BackLink, DetailList, ListPanel, PageHeader } from '@/shell/page';
import { formatMoney, formatVolume } from '@/lib/format';
import type { Alert, Fueling, FuelingFilter, FuelingStatus, Page, PageQuery } from './api';
import { AlertSeverityChip, AlertStatusChip, FuelingStatusChip } from './chips';
import { EmptyList, PagedList } from './components';
import { DateRangeFields } from './date-range';
import { useDateRange, usableRange } from './date-range-state';
import { cairoDayStart } from './input';
import { usePagedResource, useResource } from './use-resource';

export type FuelingPortal = 'ops' | 'company' | 'station';

type StatusFilter = 'ALL' | FuelingStatus;

/** The company's or station's name in the language on screen. */
function useNames() {
  const { locale } = useLocale();
  return {
    company: (fueling: Fueling) => (locale === 'ar' ? fueling.companyNameAr : fueling.companyNameEn),
    station: (fueling: Fueling) => (locale === 'ar' ? fueling.stationNameAr : fueling.stationNameEn),
  };
}

/** What the amount column shows: the amount, "not billed" for a VIP vehicle, nothing for a decline. */
function FuelingAmount({ fueling }: { fueling: Fueling }) {
  const { t } = useTranslation('live');
  if (fueling.status === 'DECLINED') return <span className="text-muted">{t('fuelings.dash')}</span>;
  if (!fueling.billable) return <span className="text-muted">{t('fuelings.notBilled')}</span>;
  return <Money value={fueling.amountPiastres} />;
}

/**
 * A page of fuelings, newest first, with status and date filters. `onOpen` decides what a row does:
 * a portal with a detail route navigates, the station portal opens a sheet.
 */
export function FuelingsList({
  portal,
  load,
  onOpen,
  title,
  description,
}: {
  portal: FuelingPortal;
  load: (page: PageQuery & FuelingFilter, signal: AbortSignal) => Promise<Page<Fueling>>;
  onOpen: (fueling: Fueling) => void;
  title: string;
  description: string;
}) {
  const { t } = useTranslation('live');
  const names = useNames();
  const [status, setStatus] = useState<StatusFilter>('ALL');
  // A day chosen on the calendar arrives as ?from=&to=.
  const [params] = useSearchParams();
  const range = useDateRange({ from: params.get('from') ?? '', to: params.get('to') ?? '' });
  const { from, to } = usableRange(range.from, range.to);
  const fromAt = cairoDayStart(from);
  // The API's upper bound is exclusive, so "to" means the end of that Cairo day.
  const toAt = cairoDayStart(to, 1);
  const fuelings = usePagedResource(
    (page, signal) => load({ ...page, status: status === 'ALL' ? undefined : status, from: fromAt, to: toAt }, signal),
    [status, fromAt, toAt],
  );
  const filtered = status !== 'ALL' || Boolean(fromAt) || Boolean(toAt);
  const { locale } = useLocale();
  const [exporting, setExporting] = useState(false);
  /** The range on screen as a CSV file; this month so far when no range is chosen. */
  const exportCsv = async () => {
    const today = new Date().toLocaleDateString('en-CA', { timeZone: 'Africa/Cairo' });
    const first = from || `${today.slice(0, 8)}01`;
    const last = to || today;
    const query = new URLSearchParams({ from: first, to: last, lang: locale });
    if (status !== 'ALL') query.set('status', status);
    setExporting(true);
    try {
      await apiDownload(`/${portal}/fuelings/export?${query.toString()}`, `zawed-fuelings-${first}-${last}.csv`);
    } catch {
      toast(t('fuelings.exportFailed'), 'danger');
    } finally {
      setExporting(false);
    }
  };
  // Ops and the station manager see the company; the company sees the station.
  const party = (fueling: Fueling) => (portal === 'company' ? names.station(fueling) : names.company(fueling));

  const columns: DataColumn<Fueling>[] = [
    { id: 'reference', header: t('fuelings.reference'), cell: (fueling) => <span dir="ltr">{fueling.reference}</span> },
    { id: 'time', header: t('fuelings.time'), cell: (fueling) => <DateTimeText value={fueling.evaluatedAt} /> },
    { id: 'plate', header: t('vehicleForm.plate'), cell: (fueling) => <Plate value={fueling.plate} size="sm" /> },
    { id: 'party', header: t(portal === 'company' ? 'fuelings.station' : 'fuelings.company'), cell: party },
    { id: 'fuel', header: t('vehicleForm.fuel'), cell: (fueling) => t(`enums.fuel.${fueling.fuelType}`) },
    { id: 'volume', header: t('fuelings.volume'), align: 'end', cell: (fueling) => <Volume value={fueling.volumeMl} /> },
    { id: 'amount', header: t('fuelings.amount'), align: 'end', cell: (fueling) => <FuelingAmount fueling={fueling} /> },
    {
      id: 'status',
      header: t('vehicle.status'),
      cell: (fueling) => <FuelingStatusChip status={fueling.status} declineReason={fueling.declineReason} origin={fueling.origin} conflicts={fueling.conflicts} />,
    },
  ];

  return (
    <>
      <PageHeader
        title={title}
        description={description}
        actions={
          <Button variant="secondary" leadingIcon={Download} loading={exporting} onClick={() => void exportCsv()}>
            {t('fuelings.export')}
          </Button>
        }
      />
      <ListPanel>
        <FilterBar
          activeCount={(status !== 'ALL' ? 1 : 0) + (range.from || range.to ? 1 : 0)}
          onReset={() => {
            setStatus('ALL');
            range.setPeriod('CUSTOM');
            range.setFrom('');
            range.setTo('');
          }}
        >
          <SegmentedControl<StatusFilter>
            label={t('fuelings.statusFilter')}
            value={status}
            onValueChange={setStatus}
            options={[
              { value: 'ALL', label: t('common.all') },
              { value: 'COMPLETED', label: t('fuelings.status.COMPLETED') },
              { value: 'DECLINED', label: t('fuelings.status.DECLINED') },
            ]}
          />
          <DateRangeFields range={range} />
        </FilterBar>
        <PagedList
          resource={fuelings}
          empty={
            filtered ? (
              <EmptyList title={t('fuelings.noResults')} description={t('fuelings.noResultsHint')} />
            ) : (
              <EmptyList title={t('fuelings.empty')} description={t('fuelings.emptyHint')} />
            )
          }
        >
          {(items) => (
            <DataView
              caption={title}
              rows={items}
              columns={columns}
              getRowId={(fueling) => fueling.id}
              onRowClick={onOpen}
              tableFrom="lg"
              renderListItem={(fueling) => (
                <ListRow
                  title={
                    <span className="flex items-center gap-2">
                      <Plate value={fueling.plate} size="sm" />
                      <span dir="ltr" className="text-xs text-muted">
                        {fueling.reference}
                      </span>
                    </span>
                  }
                  subtitle={
                    <>
                      {party(fueling)}
                      {' · '}
                      <DateTimeText value={fueling.evaluatedAt} />
                      {' · '}
                      {t(`enums.fuel.${fueling.fuelType}`)}
                      {' · '}
                      <Volume value={fueling.volumeMl} />
                    </>
                  }
                  trailing={
                    <span className="flex flex-col items-end gap-1">
                      <FuelingStatusChip status={fueling.status} declineReason={fueling.declineReason} origin={fueling.origin} conflicts={fueling.conflicts} />
                      <FuelingAmount fueling={fueling} />
                    </span>
                  }
                  onClick={() => onOpen(fueling)}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
    </>
  );
}

/** One fueling's facts. Ops sees links to the records behind it; the other portals see names only. */
export function FuelingDetail({ fueling, portal }: { fueling: Fueling; portal: FuelingPortal }) {
  const { t } = useTranslation('live');
  const names = useNames();
  const ops = portal === 'ops';
  return (
    <div className="flex flex-col gap-4">
      {fueling.conflicts.length > 0 && (
        <Banner
          tone="warning"
          title={t('fuelings.conflictsTitle')}
          description={fueling.conflicts.map((conflict) => t(`fuelings.conflict.${conflict}`)).join(' · ')}
        />
      )}
      {fueling.status === 'DECLINED' && fueling.declineReason && (
        <Banner
          tone="danger"
          title={t(`fuelings.decline.${fueling.declineReason}`)}
          description={t(`fuelings.declineHint.${fueling.declineReason}`)}
        />
      )}
      <ListPanel>
        <div className="flex justify-center border-b border-border p-5">
          <Plate value={fueling.plate} size="lg" />
        </div>
        <DetailList
          rows={[
            { label: t('fuelings.reference'), value: <span dir="ltr">{fueling.reference}</span> },
            {
              label: t('vehicle.status'),
              value: <FuelingStatusChip status={fueling.status} declineReason={fueling.declineReason} origin={fueling.origin} conflicts={fueling.conflicts} />,
            },
            portal !== 'company' && {
              label: t('fuelings.company'),
              value: ops ? (
                <Link to={`/ops/companies/${fueling.companyId}`} className="text-primary hover:underline">
                  {names.company(fueling)}
                </Link>
              ) : (
                names.company(fueling)
              ),
            },
            portal !== 'station' && {
              label: t('fuelings.station'),
              value: ops ? (
                <Link to={`/ops/stations/${fueling.stationId}`} className="text-primary hover:underline">
                  {names.station(fueling)}
                </Link>
              ) : (
                names.station(fueling)
              ),
            },
            ops && {
              label: t('fuelings.vehicle'),
              value: (
                <Link to={`/ops/vehicles/${fueling.vehicleId}`} className="text-primary hover:underline">
                  {t('fuelings.openVehicle')}
                </Link>
              ),
            },
            { label: t('vehicleForm.fuel'), value: t(`enums.fuel.${fueling.fuelType}`) },
            { label: t('fuelings.volume'), value: <Volume value={fueling.volumeMl} /> },
            { label: t('fuelings.unitPrice'), value: <Money value={fueling.unitPricePiastres} /> },
            { label: t('fuelings.amount'), value: <FuelingAmount fueling={fueling} /> },
            portal !== 'company' && { label: t('fuelings.agent'), value: <span dir="ltr">{fueling.agentWorkerId}</span> },
            { label: t('fuelings.origin'), value: t(`fuelings.originValue.${fueling.origin}`) },
            { label: t('fuelings.time'), value: <DateTimeText value={fueling.evaluatedAt} /> },
            { label: t('fuelings.deviceAt'), value: <DateTimeText value={fueling.deviceAt} /> },
            ops && { label: t('fuelings.receivedAt'), value: <DateTimeText value={fueling.receivedAt} /> },
          ]}
        />
      </ListPanel>
      {!fueling.billable && fueling.status === 'COMPLETED' && <Banner tone="info" title={t('fuelings.notBilled')} description={t('fuelings.notBilledHint')} />}
    </div>
  );
}

/** The detail route: loads the fueling named in the URL with the portal's loader. */
export function FuelingDetailPage({
  portal,
  load,
  backTo,
  renderActions,
}: {
  portal: FuelingPortal;
  load: (id: string, signal: AbortSignal) => Promise<Fueling>;
  backTo: string;
  /** What a portal may do with the fueling (Ops: request a reversal); shown beside the title. */
  renderActions?: (fueling: Fueling, reload: () => void) => ReactNode;
}) {
  const { id = '' } = useParams();
  const { t } = useTranslation('live');
  const detail = useResource((signal) => load(id, signal), [id]);

  if (detail.error && !detail.data) {
    return (
      <>
        <BackLink to={backTo} label={t('fuelings.back')} />
        <ErrorState onRetry={detail.reload} />
      </>
    );
  }
  if (!detail.data) {
    return (
      <div className="flex flex-col gap-4" aria-busy>
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40" />
      </div>
    );
  }
  return (
    <>
      <BackLink to={backTo} label={t('fuelings.back')} />
      <PageHeader
        title={t('fuelings.detailTitle', { reference: detail.data.reference })}
        actions={renderActions?.(detail.data, detail.reload)}
      />
      <div className="max-w-2xl">
        <FuelingDetail fueling={detail.data} portal={portal} />
      </div>
    </>
  );
}

const detailNumber = (details: Alert['details'], key: string): number | undefined => {
  const value = details[key];
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
};

const detailText = (details: Alert['details'], key: string): string | undefined => {
  const value = details[key];
  return typeof value === 'string' && value !== '' ? value : undefined;
};

/** The one sentence an alert's details make, in the language on screen. Falls back to the type's explanation. */
function useAlertSummary() {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  return (alert: Alert): string => {
    const d = alert.details;
    const plate = detailText(d, 'plate');
    const volume = detailNumber(d, 'volumeMl');
    const tank = detailNumber(d, 'tankCapacityMl');
    const month = detailNumber(d, 'monthVolumeMl');
    const ceiling = detailNumber(d, 'ceilingMl');
    const balance = detailNumber(d, 'balancePiastres');
    const threshold = detailNumber(d, 'thresholdPiastres');
    if (alert.type === 'TANK_CAPACITY_EXCEEDED' && plate && volume !== undefined && tank !== undefined) {
      return t('alerts.summary.TANK_CAPACITY_EXCEEDED', { plate, volume: formatVolume(volume, locale), tank: formatVolume(tank, locale) });
    }
    if (alert.type === 'VIP_CEILING_PASSED' && plate && month !== undefined && ceiling !== undefined) {
      return t('alerts.summary.VIP_CEILING_PASSED', { plate, month: formatVolume(month, locale), ceiling: formatVolume(ceiling, locale) });
    }
    if (alert.type === 'LOW_BALANCE' && balance !== undefined && threshold !== undefined) {
      return t('alerts.summary.LOW_BALANCE', { balance: formatMoney(balance, locale), threshold: formatMoney(threshold, locale) });
    }
    return t(`alerts.explain.${alert.type}`);
  };
}

/**
 * One alert: what happened, how serious, whether anyone has dealt with it, and a way to the
 * fueling that raised it. `actions` is where Ops puts "acknowledge".
 */
export function AlertRow({
  alert,
  fuelingPath,
  context,
  actions,
}: {
  alert: Alert;
  /** Where the fueling lives in this portal, when the alert names one. */
  fuelingPath?: string;
  /** Who it is about (the company, for Ops). */
  context?: ReactNode;
  actions?: ReactNode;
}) {
  const { t } = useTranslation('live');
  const summarize = useAlertSummary();
  return (
    <div className="flex flex-col gap-2 px-4 py-3 md:flex-row md:items-center md:justify-between md:gap-4">
      <div className="flex min-w-0 flex-col gap-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium">{t(`alerts.type.${alert.type}`)}</span>
          <AlertSeverityChip severity={alert.severity} />
          <AlertStatusChip status={alert.status} />
        </div>
        <p className="text-sm text-muted">{summarize(alert)}</p>
        <p className="text-xs text-muted">
          {context}
          {context && ' · '}
          <DateTimeText value={alert.createdAt} />
          {alert.status === 'ACKNOWLEDGED' && alert.acknowledgedAt && (
            <>
              {' · '}
              {t('alerts.acknowledgedAt')} <DateTimeText value={alert.acknowledgedAt} />
            </>
          )}
        </p>
      </div>
      <div className="flex shrink-0 flex-wrap items-center gap-2">
        {fuelingPath && alert.fuelingId && (
          <Button asChild size="sm" variant="secondary">
            <Link to={`${fuelingPath}/${alert.fuelingId}`}>{t('alerts.openFueling')}</Link>
          </Button>
        )}
        {actions}
      </div>
    </div>
  );
}
