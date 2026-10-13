import { CircleCheck, Clock } from 'lucide-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, DateTimeText, Icon, List, ListRow, Money, Plate, Volume } from '@/design-system';
import { receiptFromQueued, receiptFromTransaction, type ReceiptModel } from '@/features/receipt/model';
import { useNow } from '@/lib/use-now';
import { isToday } from '@/mocks/selectors';
import { useDemoStore } from '@/mocks/store';

interface HistoryRow {
  key: string;
  receipt: ReceiptModel;
  pending: boolean;
}

/** Today's transactions by this agent, newest first, with sync status and reprint (PRD §6.1). */
export function HistoryScreen({ agentId, onOpen, onBack }: { agentId: string; onOpen: (receipt: ReceiptModel) => void; onBack: () => void }) {
  const { t } = useTranslation('pos');
  const now = useNow();
  const data = useDemoStore((state) => state.data);
  const queue = useDemoStore((state) => state.posQueue);
  const rows = useMemo<HistoryRow[]>(() => {
    const pending = queue.map((item) => ({ key: item.request.clientTxnId, receipt: receiptFromQueued(data, item), pending: true }));
    const done = data.transactions
      .filter((txn) => txn.agentId === agentId && txn.status === 'completed' && isToday(txn.deviceAt, now))
      .map((txn) => ({ key: txn.id, receipt: receiptFromTransaction(data, txn), pending: false }));
    return [...pending, ...done].sort((a, b) => b.receipt.at.localeCompare(a.receipt.at));
  }, [data, queue, agentId, now]);

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <h1 className="px-4 pt-4 pb-2 text-lg font-semibold">{t('history.title')}</h1>
      <div className="min-h-0 flex-1 overflow-y-auto">
        {rows.length === 0 ? (
          <p className="px-4 py-8 text-center text-muted">{t('history.empty')}</p>
        ) : (
          <List>
            {rows.map((row) => (
              <ListRow
                key={row.key}
                onClick={() => onOpen(row.receipt)}
                leading={
                  <span className={row.pending ? 'text-warning' : 'text-success'} title={t(row.pending ? 'success.pendingSync' : 'success.synced')}>
                    <Icon icon={row.pending ? Clock : CircleCheck} size={20} />
                  </span>
                }
                title={<Plate value={row.receipt.plate} variant="plain" />}
                subtitle={
                  <>
                    <DateTimeText value={row.receipt.at} variant="time" /> · <Volume value={row.receipt.volumeMl} />
                  </>
                }
                trailing={row.receipt.billable ? <Money value={row.receipt.amountPiastres} /> : undefined}
              />
            ))}
          </List>
        )}
      </div>
      <div className="px-4 pt-2 pb-4">
        <Button variant="secondary" size="lg" fullWidth onClick={onBack}>
          {t('history.back')}
        </Button>
      </div>
    </div>
  );
}
