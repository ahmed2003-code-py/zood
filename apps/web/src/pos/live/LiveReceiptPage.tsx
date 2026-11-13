/**
 * The digital receipt behind the QR code (B4 contract §6). Public: the signed link is the proof,
 * so no sign-in, and the API answers 404 for anything else.
 */
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useSearchParams } from 'react-router';
import type { components } from '@/api/generated/schema';
import { apiRequest } from '@/api/http-client';
import { Skeleton } from '@/design-system';
import { ThermalReceipt } from '@/features/receipt/ThermalReceipt';
import type { ReceiptModel } from '@/features/receipt/model';
import { i18n } from '@/i18n';
import type { Locale } from '@/lib/locale';

type Receipt = components['schemas']['ReceiptResponse'];

function modelOf(receipt: Receipt, link: string): ReceiptModel {
  const fuel = (locale: Locale) => i18n.getFixedT(locale, 'live')(`enums.fuel.${receipt.fuelType}`);
  return {
    reference: receipt.reference,
    clientTxnId: receipt.reference,
    qrUrl: link,
    stationName: { ar: receipt.stationNameAr, en: receipt.stationNameEn },
    timeZone: 'Africa/Cairo',
    at: receipt.evaluatedAt,
    plate: receipt.plate,
    fuelName: { ar: fuel('ar'), en: fuel('en') },
    volumeMl: receipt.volumeMl,
    unitPricePiastres: receipt.unitPricePiastres,
    amountPiastres: receipt.amountPiastres,
    billable: receipt.billable,
    locale: receipt.receiptLocale === 'en' ? 'en' : 'ar',
    pendingSync: false,
  };
}

export function LiveReceiptPage() {
  const { t } = useTranslation('receipt');
  const { id = '' } = useParams();
  const [params] = useSearchParams();
  const sig = params.get('sig') ?? '';
  const [state, setState] = useState<{ status: 'loading' } | { status: 'error' } | { status: 'ready'; receipt: Receipt }>({
    status: 'loading',
  });

  useEffect(() => {
    const controller = new AbortController();
    apiRequest<Receipt>(`/receipts/${encodeURIComponent(id)}?sig=${encodeURIComponent(sig)}`, {
      anonymous: true,
      signal: controller.signal,
    })
      .then((receipt) => setState({ status: 'ready', receipt }))
      .catch(() => {
        if (!controller.signal.aborted) setState({ status: 'error' });
      });
    return () => controller.abort();
  }, [id, sig]);

  return (
    <main className="flex min-h-dvh flex-col items-center gap-4 bg-surface-sunken px-4 py-8">
      <h1 className="text-base font-semibold">{t('title')}</h1>
      {state.status === 'loading' && <Skeleton className="h-96 w-64" />}
      {state.status === 'error' && (
        <p role="alert" className="text-sm text-muted">
          {t('notFound')}
        </p>
      )}
      {state.status === 'ready' && <ThermalReceipt model={modelOf(state.receipt, window.location.href)} />}
    </main>
  );
}
