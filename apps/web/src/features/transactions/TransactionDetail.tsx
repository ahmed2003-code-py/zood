import { ReceiptText, WifiOff } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { FuelTransaction } from '@/domain/types';
import { Button, DateTimeText, Money, Plate, Sheet, StatusChip, TransactionStatusChip, Volume } from '@/design-system';
import { receiptFromTransaction } from '@/features/receipt/model';
import { ThermalReceipt } from '@/features/receipt/ThermalReceipt';
import { useLocale } from '@/i18n/useLocale';
import { formatMoney } from '@/lib/format';
import { localize } from '@/lib/locale';
import { useDemoStore } from '@/mocks/store';
import { DetailList, Disclosure, ListPanel } from '@/shell/page';

/**
 * Transaction details with progressive disclosure: what matters first (status, amount, vehicle,
 * station, fuel, liters, time), technical detail (ids, origin, sync times, ledger) behind "More".
 */
export function TransactionDetail({ transaction: txn, showCompany = false }: { transaction: FuelTransaction; showCompany?: boolean }) {
  const { t } = useTranslation(['transactions', 'status']);
  const { locale } = useLocale();
  const data = useDemoStore((state) => state.data);
  const vehicle = data.vehicles[txn.vehicleId];
  const station = data.stations[txn.stationId];
  const agent = data.agents[txn.agentId];
  const device = data.devices[txn.deviceId];

  return (
    <div className="flex max-w-2xl flex-col gap-6">
      <section className="flex flex-col items-start gap-3">
        <div className="flex flex-wrap items-center gap-2">
          <TransactionStatusChip status={txn.status} size="md" />
          {txn.origin === 'offline' && <StatusChip tone="neutral" icon={WifiOff} label={t('offline')} />}
          {txn.declineReason && <StatusChip tone="danger" label={t(`status:decline.${txn.declineReason}`)} />}
        </div>
        <p className="text-4xl font-semibold">
          {txn.billable ? <Money value={txn.amountPiastres} /> : <span className="text-2xl">{t('nonBillable')}</span>}
        </p>
        {vehicle && <Plate value={vehicle.plate} size="md" />}
      </section>

      <ListPanel>
        <DetailList
          rows={[
            showCompany && { label: t('columns.company'), value: localize(data.companies[txn.companyId]?.name, locale) },
            { label: t('columns.station'), value: localize(station?.name, locale) },
            { label: t('columns.fuel'), value: localize(data.fuelTypes[txn.fuelTypeId]?.name, locale) },
            { label: t('columns.volume'), value: <Volume value={txn.volumeMl} /> },
            { label: t('columns.time'), value: <DateTimeText value={txn.deviceAt} /> },
          ]}
        />
      </ListPanel>

      <Disclosure summary={t('detail.more')}>
        <DetailList
          rows={[
            { label: t('detail.price'), value: formatMoney(txn.unitPricePiastres, locale) },
            { label: t('detail.reference'), value: <bdi dir="ltr">{txn.shortId}</bdi> },
            { label: t('detail.origin'), value: t(txn.origin === 'offline' ? 'detail.originOffline' : 'detail.originOnline') },
            { label: t('detail.fueledAt'), value: <DateTimeText value={txn.deviceAt} /> },
            txn.syncedAt && { label: t('detail.syncedAt'), value: <DateTimeText value={txn.syncedAt} /> },
            !txn.syncedAt && txn.receivedAt && { label: t('detail.receivedAt'), value: <DateTimeText value={txn.receivedAt} /> },
            { label: t('detail.agent'), value: localize(agent?.name, locale) },
            device && { label: t('detail.device'), value: <bdi dir="ltr">{device.code}</bdi> },
            txn.ledgerEntryId && { label: t('detail.ledger'), value: <bdi dir="ltr">{txn.ledgerEntryId.slice(0, 8).toUpperCase()}</bdi> },
          ]}
        />
      </Disclosure>

      {txn.status === 'completed' && (
        <Sheet
          title={t('detail.receipt')}
          trigger={
            <Button variant="secondary" leadingIcon={ReceiptText} className="self-start">
              {t('detail.viewReceipt')}
            </Button>
          }
        >
          <div className="flex justify-center rounded-container bg-surface-sunken py-6">
            <ThermalReceipt model={receiptFromTransaction(data, txn)} />
          </div>
        </Sheet>
      )}
    </div>
  );
}
