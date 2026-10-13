import { WifiOff } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { FuelTransaction } from '@/domain/types';
import { DataView, DateTimeText, Icon, List, ListRow, Money, Plate, TransactionStatusChip, Volume, type DataColumn } from '@/design-system';
import { ARRIVAL_CLASS, useArrivals } from '@/features/live/use-arrivals';
import { useLocale } from '@/i18n/useLocale';
import { localize } from '@/lib/locale';
import { useDemoStore } from '@/mocks/store';

export type TransactionPerspective = 'company' | 'station' | 'ops';

interface TransactionListProps {
  transactions: FuelTransaction[];
  perspective: TransactionPerspective;
  /** compact: always rows (for summaries on home screens), never a table. */
  compact?: boolean;
  onOpen?: (transaction: FuelTransaction) => void;
}

/**
 * Transactions as a table on desktop and purpose-built rows on mobile. Columns adapt to who is
 * looking: a company sees stations, a station sees companies, Operations sees both.
 * Transactions that arrive while the screen is open (e.g. from the POS) are briefly highlighted.
 */
export function TransactionList({ transactions, perspective, compact = false, onOpen }: TransactionListProps) {
  const { t } = useTranslation('transactions');
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const isNew = useArrivals(transactions.map((txn) => txn.id));
  const rowClass = (txn: FuelTransaction) => (isNew(txn.id) ? ARRIVAL_CLASS : undefined);

  const plate = (txn: FuelTransaction) => data.vehicles[txn.vehicleId]?.plate ?? '';
  const station = (txn: FuelTransaction) => localize(data.stations[txn.stationId]?.name, locale);
  const company = (txn: FuelTransaction) => localize(data.companies[txn.companyId]?.name, locale);
  const fuel = (txn: FuelTransaction) => localize(data.fuelTypes[txn.fuelTypeId]?.name, locale);

  const all: Record<string, DataColumn<FuelTransaction>> = {
    id: {
      id: 'id',
      header: t('columns.id'),
      cell: (txn) => (
        <span className="flex items-center gap-1.5 font-medium tabular-nums">
          <bdi dir="ltr">{txn.shortId}</bdi>
          {txn.origin === 'offline' && (
            <span title={t('offline')} className="text-muted">
              <Icon icon={WifiOff} size={14} />
            </span>
          )}
        </span>
      ),
    },
    vehicle: { id: 'vehicle', header: t('columns.vehicle'), cell: (txn) => <Plate value={plate(txn)} size="sm" /> },
    company: { id: 'company', header: t('columns.company'), cell: company },
    station: { id: 'station', header: t('columns.station'), cell: station },
    fuel: { id: 'fuel', header: t('columns.fuel'), cell: fuel },
    volume: { id: 'volume', header: t('columns.volume'), align: 'end', cell: (txn) => <Volume value={txn.volumeMl} /> },
    amount: {
      id: 'amount',
      header: t('columns.amount'),
      align: 'end',
      cell: (txn) =>
        txn.billable ? <Money value={txn.amountPiastres} /> : <span className="text-xs text-muted">{t('nonBillable')}</span>,
    },
    status: { id: 'status', header: t('columns.status'), cell: (txn) => <TransactionStatusChip status={txn.status} /> },
    time: { id: 'time', header: t('columns.time'), cell: (txn) => <DateTimeText value={txn.deviceAt} className="text-muted" /> },
  };
  const order: Record<TransactionPerspective, string[]> = {
    company: ['id', 'vehicle', 'station', 'fuel', 'volume', 'amount', 'status', 'time'],
    station: ['time', 'vehicle', 'company', 'fuel', 'volume', 'amount', 'status'],
    ops: ['id', 'company', 'station', 'vehicle', 'amount', 'status', 'time'],
  };
  const columns = order[perspective].map((id) => all[id]).filter((column): column is DataColumn<FuelTransaction> => Boolean(column));

  const renderRow = (txn: FuelTransaction) => (
    <ListRow
      chevron={Boolean(onOpen)}
      onClick={onOpen ? () => onOpen(txn) : undefined}
      title={perspective === 'ops' ? company(txn) : <Plate value={plate(txn)} variant="plain" />}
      subtitle={
        <>
          {perspective === 'station' ? company(txn) : station(txn)} · <DateTimeText value={txn.deviceAt} variant="time" />
        </>
      }
      trailing={txn.billable ? <Money value={txn.amountPiastres} /> : <Volume value={txn.volumeMl} />}
      trailingSub={<TransactionStatusChip status={txn.status} />}
    />
  );

  if (compact) {
    return (
      <List>
        {transactions.map((txn) => (
          <div key={txn.id} className={rowClass(txn)}>
            {renderRow(txn)}
          </div>
        ))}
      </List>
    );
  }

  return (
    <DataView
      caption={t('caption')}
      rows={transactions}
      columns={columns}
      getRowId={(txn) => txn.id}
      renderListItem={renderRow}
      onRowClick={onOpen}
      rowClassName={rowClass}
    />
  );
}
