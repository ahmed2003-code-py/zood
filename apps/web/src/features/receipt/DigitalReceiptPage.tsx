import { Clock } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';
import { EmptyState, StatusChip, ZoodWordmark } from '@/design-system';
import { TransactionDetail } from '@/features/transactions/TransactionDetail';
import { useDemoStore } from '@/mocks/store';
import { receiptFromQueued } from './model';
import { ThermalReceipt } from './ThermalReceipt';

/**
 * /receipt/:clientTxnId — the digital receipt behind the printed QR code (also the fallback when
 * printing fails). Production: an opaque, signed, expiring link showing only receipt-level data.
 */
export function DigitalReceiptPage() {
  const { t } = useTranslation('receipt');
  const { id = '' } = useParams();
  const transaction = useDemoStore((state) => state.data.transactions.find((txn) => txn.clientTxnId === id));
  const queued = useDemoStore((state) => state.posQueue.find((item) => item.request.clientTxnId === id));
  const data = useDemoStore((state) => state.data);

  return (
    <div className="mx-auto flex min-h-dvh max-w-2xl flex-col gap-6 px-4 py-6">
      <header className="flex items-center justify-between">
        <ZoodWordmark size="sm" />
        <h1 className="text-base font-semibold">{t('digitalTitle')}</h1>
      </header>
      {transaction ? (
        <TransactionDetail transaction={transaction} />
      ) : queued ? (
        <div className="flex flex-col items-center gap-4">
          <StatusChip tone="warning" icon={Clock} size="md" label={t('pendingSync')} />
          <p className="text-center text-sm text-muted">{t('pendingNote')}</p>
          <ThermalReceipt model={receiptFromQueued(data, queued)} />
        </div>
      ) : (
        <EmptyState title={t('notFound')} description="" />
      )}
    </div>
  );
}
