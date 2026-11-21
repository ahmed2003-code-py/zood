/**
 * Station portal: the pump agents' shifts with what each one dispensed (fuelings, litres, value),
 * filtered by station, agent or terminal; a shift opens its own fuelings.
 *
 * The shifts API carries no totals, so the screen loads the fuelings of the period the listed shifts
 * cover (at most MAX_PAGES pages) and adds each to the shift of its terminal and agent at the time
 * it was pumped (station-model.ts). When there are more fuelings than that, the totals say so.
 */
import { useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { ExternalLink } from 'lucide-react';
import { Banner, Button, DataView, EmptyState, ListRow, Money, Plate, SelectField, Sheet, Skeleton, StatusChip, Volume, type DataColumn } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatMoney, formatNumber, formatVolume } from '@/lib/format';
import { cairoDay } from '@/lib/periods';
import { ListPanel, PageHeader } from '@/shell/page';
import { stationApi, type Fueling, type Shift } from '../api';
import { FuelingStatusChip } from '../chips';
import { PagedList } from '../components';
import { usePagedResource, useResource } from '../use-resource';
import { useStationName } from './hooks';
import { ListFilters, Stamp, StationSelect } from './parts';
import { EMPTY_TOTALS, inShift, shiftMinutes, shiftsWindow, shiftTotals, type ShiftTotals } from './station-model';

const MAX_PAGES = 10;
const PAGE = 100;

/** The fuelings that fall in the listed shifts' period, as far as MAX_PAGES pages go. */
function useShiftFuelings(shifts: readonly Shift[], stationId: string) {
  const now = useMemo(() => new Date(), []);
  const span = shiftsWindow(shifts, now);
  return useResource(
    async (signal) => {
      if (!span) return { items: [] as Fueling[], partial: false };
      const items: Fueling[] = [];
      let cursor: string | undefined;
      for (let page = 0; page < MAX_PAGES; page++) {
        const answer = await stationApi.fuelings({ from: span.from, to: span.to, stationId: stationId || undefined, limit: PAGE, cursor }, signal);
        items.push(...answer.items);
        cursor = answer.nextCursor ?? undefined;
        if (!cursor) return { items, partial: false };
      }
      return { items, partial: true };
    },
    [span?.from, span?.to, stationId],
  );
}

export function StationShiftsPage() {
  const { t } = useTranslation(['station', 'live']);
  const { locale } = useLocale();
  const [stationId, setStationId] = useState('');
  const [agentId, setAgentId] = useState('');
  const [deviceId, setDeviceId] = useState('');
  const [open, setOpen] = useState<Shift>();
  const now = useMemo(() => new Date(), []);

  const stations = useResource((signal) => stationApi.stations({ limit: 100 }, signal), []);
  const agents = useResource((signal) => stationApi.agents({ limit: 100, stationId: stationId || undefined }, signal), [stationId]);
  const devices = useResource((signal) => stationApi.devices({ limit: 100, stationId: stationId || undefined }, signal), [stationId]);
  const shifts = usePagedResource(
    (page, signal) => stationApi.shifts({ ...page, stationId: stationId || undefined, agentId: agentId || undefined, deviceId: deviceId || undefined }, signal),
    [stationId, agentId, deviceId],
  );
  const fuelings = useShiftFuelings(shifts.items, stationId);
  const totals = useMemo(() => (fuelings.data ? shiftTotals(shifts.items, fuelings.data.items, now) : undefined), [fuelings.data, shifts.items, now]);
  const stationName = useStationName(stations.data?.items);
  const deviceName = (shift: Shift) => {
    const device = devices.data?.items.find((item) => item.id === shift.deviceId);
    return device?.label ?? shift.deviceCode;
  };
  const filters = (stationId ? 1 : 0) + (agentId ? 1 : 0) + (deviceId ? 1 : 0);

  const totalsOf = (shift: Shift): ShiftTotals | undefined => totals?.get(shift.id) ?? (totals ? EMPTY_TOTALS : undefined);
  const figure = (shift: Shift, pick: (line: ShiftTotals) => ReactNode) => {
    const line = totalsOf(shift);
    if (line) return pick(line);
    return fuelings.error ? <span className="text-muted">{t('live:fuelings.dash')}</span> : <Skeleton className="ms-auto h-4 w-14" />;
  };
  const duration = (shift: Shift) => {
    const minutes = shiftMinutes(shift, now);
    return t('live.shifts.duration', { hours: Math.floor(minutes / 60), minutes: String(minutes % 60).padStart(2, '0') });
  };

  const columns: DataColumn<Shift>[] = [
    {
      id: 'agent',
      header: t('live.shifts.agent'),
      cell: (shift) => (
        <span className="flex flex-col">
          <span className="font-medium">{shift.agentName}</span>
          <bdi dir="ltr" className="font-mono text-xs text-muted">
            {shift.workerId}
          </bdi>
        </span>
      ),
    },
    {
      id: 'terminal',
      header: t('live.shifts.terminal'),
      cell: (shift) => (
        <span className="flex flex-col">
          <span>{deviceName(shift)}</span>
          <span className="text-xs text-muted">{stationName(shift.stationId)}</span>
        </span>
      ),
    },
    {
      id: 'time',
      header: t('live.shifts.time'),
      cell: (shift) => (
        <span className="flex flex-col">
          <Stamp value={shift.startedAt} />
          <span className="text-xs text-muted">{shift.endedAt ? duration(shift) : t('live.shifts.runningFor', { duration: duration(shift) })}</span>
        </span>
      ),
    },
    { id: 'fuelings', header: t('live.shifts.fuelings'), align: 'end', cell: (shift) => figure(shift, (line) => formatNumber(line.fuelings, locale)) },
    { id: 'litres', header: t('live.shifts.litres'), align: 'end', cell: (shift) => figure(shift, (line) => <Volume value={line.volumeMl} fractionDigits={0} />) },
    { id: 'amount', header: t('live.shifts.amount'), align: 'end', cell: (shift) => figure(shift, (line) => <Money value={line.amountPiastres} />) },
    {
      id: 'status',
      header: t('live.shifts.status'),
      cell: (shift) => (shift.endedAt ? <StatusChip tone="neutral" label={t(`live.shifts.ended.${shift.endReason ?? 'OTHER'}`, { defaultValue: t('live.shifts.ended.OTHER') })} /> : <StatusChip tone="success" label={t('live.shifts.open')} />),
    },
  ];

  return (
    <>
      <PageHeader title={t('live.shifts.title')} description={t('live.shifts.description')} />
      <ListPanel>
        <ListFilters
          active={filters}
          onClear={() => {
            setStationId('');
            setAgentId('');
            setDeviceId('');
          }}
        >
          <StationSelect
            stations={stations.data?.items ?? []}
            value={stationId}
            onChange={(next) => {
              setStationId(next);
              setAgentId('');
              setDeviceId('');
            }}
          />
          <SelectField
            className="md:w-56"
            label={t('live.shifts.agent')}
            value={agentId || 'ALL'}
            onValueChange={(next) => setAgentId(next === 'ALL' ? '' : next)}
            options={[{ value: 'ALL', label: t('live.filters.allAgents') }, ...(agents.data?.items ?? []).map((agent) => ({ value: agent.id, label: agent.fullName }))]}
          />
          <SelectField
            className="md:w-56"
            label={t('live.shifts.terminal')}
            value={deviceId || 'ALL'}
            onValueChange={(next) => setDeviceId(next === 'ALL' ? '' : next)}
            options={[
              { value: 'ALL', label: t('live.filters.allTerminals') },
              ...(devices.data?.items ?? []).map((device) => ({ value: device.id, label: device.label ? `${device.label} (${device.code})` : device.code })),
            ]}
          />
        </ListFilters>
        {fuelings.data?.partial && <Banner tone="info" title={t('live.shifts.partial')} className="m-4" />}
        <PagedList
          resource={shifts}
          empty={
            filters > 0 ? (
              <EmptyState title={t('live.shifts.noResults')} description={t('live.shifts.noResultsHint')} />
            ) : (
              <EmptyState title={t('live:shifts.empty')} description={t('live:shifts.emptyHint')} />
            )
          }
        >
          {(items) => (
            <DataView
              caption={t('live.shifts.title')}
              rows={items}
              columns={columns}
              getRowId={(shift) => shift.id}
              onRowClick={setOpen}
              tableFrom="lg"
              renderListItem={(shift) => (
                <ListRow
                  title={
                    <span className="flex items-center gap-2">
                      {shift.agentName}
                      {!shift.endedAt && <StatusChip tone="success" label={t('live.shifts.open')} />}
                    </span>
                  }
                  subtitle={
                    <>
                      {deviceName(shift)} · <Stamp value={shift.startedAt} />
                    </>
                  }
                  trailing={figure(shift, (line) => <Money value={line.amountPiastres} />)}
                  trailingSub={figure(shift, (line) => (
                    <span className="text-xs text-muted">
                      {t('live.shifts.countShort', { count: line.fuelings })} · {formatVolume(line.volumeMl, locale, 0)}
                    </span>
                  ))}
                  onClick={() => setOpen(shift)}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
      <ShiftSheet
        shift={open}
        onClose={() => setOpen(undefined)}
        totals={open ? totalsOf(open) : undefined}
        fuelings={open && fuelings.data ? fuelings.data.items.filter((fueling) => inShift(open, fueling, now)) : undefined}
        terminal={open ? deviceName(open) : ''}
        station={open ? stationName(open.stationId) : undefined}
        duration={open ? duration(open) : ''}
      />
    </>
  );
}

function ShiftSheet({
  shift,
  onClose,
  totals,
  fuelings,
  terminal,
  station,
  duration,
}: {
  shift?: Shift;
  onClose: () => void;
  totals?: ShiftTotals;
  fuelings?: Fueling[];
  terminal: string;
  station?: string;
  duration: string;
}) {
  const { t } = useTranslation(['station', 'live']);
  const { locale } = useLocale();
  const navigate = useNavigate();
  const openList = () => {
    if (!shift) return;
    const from = cairoDay(new Date(shift.startedAt));
    const to = cairoDay(shift.endedAt ? new Date(shift.endedAt) : new Date());
    navigate(`/station/transactions?from=${from}&to=${to}&stationId=${shift.stationId}`);
  };
  return (
    <Sheet
      open={Boolean(shift)}
      onOpenChange={(next) => !next && onClose()}
      size="lg"
      title={shift ? t('live.shifts.detailTitle', { agent: shift.agentName }) : ''}
      footer={
        <Button variant="secondary" leadingIcon={ExternalLink} onClick={openList}>
          {t('live.shifts.openFuelings')}
        </Button>
      }
    >
      {shift && (
        <div className="flex flex-col gap-5">
          <div className="grid grid-cols-3 gap-3">
            <Figure label={t('live.shifts.fuelings')} value={totals ? formatNumber(totals.fuelings, locale) : undefined} />
            <Figure label={t('live.shifts.litres')} value={totals ? formatVolume(totals.volumeMl, locale, 0) : undefined} />
            <Figure label={t('live.shifts.amount')} value={totals ? formatMoney(totals.amountPiastres, locale) : undefined} />
          </div>
          <dl className="divide-y divide-border rounded-container border border-border text-sm">
            <Fact label={t('live.shifts.agent')}>
              {shift.agentName} · <bdi dir="ltr">{shift.workerId}</bdi>
            </Fact>
            <Fact label={t('live.shifts.terminal')}>
              {terminal} · <bdi dir="ltr">{shift.deviceCode}</bdi>
            </Fact>
            {station && <Fact label={t('live.filters.station')}>{station}</Fact>}
            <Fact label={t('live.shifts.started')}>
              <Stamp value={shift.startedAt} />
            </Fact>
            <Fact label={t('live.shifts.endedAt')}>
              {shift.endedAt ? <Stamp value={shift.endedAt} /> : t('live.shifts.open')}
            </Fact>
            <Fact label={t('live.shifts.length')}>{duration}</Fact>
            {totals && totals.declined > 0 && <Fact label={t('live.shifts.declined')}>{formatNumber(totals.declined, locale)}</Fact>}
          </dl>
          <section>
            <h3 className="mb-2 text-sm font-semibold">{t('live.shifts.itsFuelings')}</h3>
            {!fuelings ? (
              <Skeleton className="h-24" />
            ) : fuelings.length === 0 ? (
              <p className="text-sm text-muted">{t('live.shifts.noFuelings')}</p>
            ) : (
              <ul className="divide-y divide-border rounded-container border border-border">
                {fuelings.map((fueling) => (
                  <li key={fueling.id} className="flex items-center justify-between gap-3 px-4 py-2.5">
                    <div className="min-w-0">
                      <Plate value={fueling.plate} size="sm" />
                      <p className="mt-1 text-xs text-muted">
                        <Stamp value={fueling.deviceAt} variant="time" /> · <bdi dir="ltr">{fueling.reference}</bdi>
                      </p>
                    </div>
                    <div className="flex flex-col items-end gap-1 text-sm tabular-nums">
                      {fueling.status === 'COMPLETED' ? <Money value={fueling.amountPiastres} /> : null}
                      <FuelingStatusChip status={fueling.status} declineReason={fueling.declineReason} origin={fueling.origin} conflicts={fueling.conflicts} />
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </div>
      )}
    </Sheet>
  );
}

function Figure({ label, value }: { label: string; value?: string }) {
  return (
    <div className="rounded-container border border-border bg-surface-sunken p-3">
      <p className="text-xs text-muted">{label}</p>
      {value === undefined ? <Skeleton className="mt-1 h-6" /> : <p className="text-lg font-semibold tabular-nums">{value}</p>}
    </div>
  );
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-3 px-4 py-2.5">
      <dt className="text-muted">{label}</dt>
      <dd className="text-end font-medium">{children}</dd>
    </div>
  );
}
