/**
 * Pump agents' shifts, shared by Ops and the station portal: who signed in on which terminal, when,
 * and when it ended. A row opens the fuelings of the days the shift ran, where the list can be
 * narrowed further.
 */
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { DataView, DateTimeText, ListRow, StatusChip, type DataColumn } from '@/design-system';
import { ListPanel, PageHeader } from '@/shell/page';
import type { Page, PageQuery, Shift, Station } from './api';
import { EmptyList, PagedList } from './components';
import { usePagedResource, useStationNames } from './use-resource';

/** The Cairo calendar day of an instant, as yyyy-mm-dd: what the fueling list's date filters take. */
const cairoDay = (value: string | Date) => new Date(value).toLocaleDateString('en-CA', { timeZone: 'Africa/Cairo' });

export function ShiftsList({
  base,
  load,
  loadStations,
  title,
  description,
}: {
  /** The portal's path prefix, where its fuelings list lives (`/ops` or `/station`). */
  base: string;
  load: (page: PageQuery, signal: AbortSignal) => Promise<Page<Shift>>;
  loadStations: (signal: AbortSignal) => Promise<Page<Station>>;
  title: string;
  description: string;
}) {
  const { t } = useTranslation('live');
  const navigate = useNavigate();
  const shifts = usePagedResource(load, []);
  const stationName = useStationNames(loadStations);

  const open = (shift: Shift) => {
    const from = cairoDay(shift.startedAt);
    const to = cairoDay(shift.endedAt ?? new Date());
    navigate(`${base}/transactions?from=${from}&to=${to}`);
  };
  const ended = (shift: Shift) =>
    shift.endedAt ? <DateTimeText value={shift.endedAt} /> : <StatusChip tone="success" label={t('shifts.open')} />;
  const agent = (shift: Shift) => (
    <span className="flex flex-col">
      <span>{shift.agentName}</span>
      <span dir="ltr" className="font-mono text-xs text-muted">
        {shift.workerId}
      </span>
    </span>
  );

  const columns: DataColumn<Shift>[] = [
    { id: 'agent', header: t('shifts.agent'), cell: agent },
    { id: 'device', header: t('shifts.device'), cell: (shift) => <span dir="ltr">{shift.deviceCode}</span> },
    { id: 'station', header: t('shifts.station'), cell: (shift) => stationName(shift.stationId) ?? t('fuelings.dash') },
    { id: 'start', header: t('shifts.started'), cell: (shift) => <DateTimeText value={shift.startedAt} /> },
    { id: 'end', header: t('shifts.ended'), cell: ended },
  ];

  return (
    <>
      <PageHeader title={title} description={description} />
      <ListPanel>
        <PagedList resource={shifts} empty={<EmptyList title={t('shifts.empty')} description={t('shifts.emptyHint')} />}>
          {(items) => (
            <DataView
              caption={title}
              rows={items}
              columns={columns}
              getRowId={(shift) => shift.id}
              onRowClick={open}
              tableFrom="lg"
              renderListItem={(shift) => (
                <ListRow
                  title={shift.agentName}
                  subtitle={
                    <>
                      <span dir="ltr">{shift.deviceCode}</span>
                      {' · '}
                      <DateTimeText value={shift.startedAt} />
                      {shift.endedAt && (
                        <>
                          {' – '}
                          <DateTimeText value={shift.endedAt} />
                        </>
                      )}
                    </>
                  }
                  trailing={shift.endedAt ? undefined : <StatusChip tone="success" label={t('shifts.open')} />}
                  onClick={() => open(shift)}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
    </>
  );
}
