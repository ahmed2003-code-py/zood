import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/cn';
import { BRAND_NAME } from '@/lib/brand';
import { formatMoney, formatMoneyValue, formatNumericDate, formatTime, formatVolume } from '@/lib/format';
import { LOCALE_DIRECTION, localize } from '@/lib/locale';
import { digitalReceiptPath, type ReceiptModel } from './model';
import { QrCode } from './QrCode';

// Printed glyph (font-independent check mark), not translatable text.
const CHECK_MARK = '✓';

/**
 * Compact 58 mm thermal receipt preview (≈ 48 mm printable width). Intentionally short:
 * identity, station, time and reference, vehicle and fuel, quantity, total, result, small QR.
 * Paper and ink are fixed (white/black) because this represents the physical print.
 */
export function ThermalReceipt({ model, printing = false, className }: { model: ReceiptModel; printing?: boolean; className?: string }) {
  const { i18n } = useTranslation();
  const t = i18n.getFixedT(model.locale, 'receipt');
  const locale = model.locale;
  const qrValue = `${window.location.origin}${digitalReceiptPath(model.clientTxnId)}`;

  return (
    <figure
      dir={LOCALE_DIRECTION[locale]}
      lang={locale}
      aria-label={t('paperLabel')}
      className={cn(
        'w-[14.5rem] bg-paper px-4 pt-4 pb-5 text-[0.75rem] leading-[1.35] text-paper-ink shadow-elevated',
        printing && 'animate-[zood-print_1.4s_ease-out]',
        className,
      )}
    >
      <p className="text-center text-sm font-bold">
        <span lang="ar">{BRAND_NAME.ar}</span> · <span lang="en">{BRAND_NAME.en}</span>
      </p>
      <p className="text-center">{localize(model.stationName, locale)}</p>
      <p className="mt-1 flex justify-between gap-2">
        <span>
          <bdi dir="ltr">{formatNumericDate(model.at, model.timeZone)}</bdi> {formatTime(model.at, locale, model.timeZone)}
        </span>
        <bdi dir="ltr">#{model.reference.replace(/^TX-/, '')}</bdi>
      </p>
      <p className="mt-1 flex justify-between gap-2 font-semibold">
        <bdi dir="rtl" lang="ar">
          {model.plate}
        </bdi>
        <span>{localize(model.fuelName, locale)}</span>
      </p>
      <p>
        <bdi>{formatVolume(model.volumeMl, locale)}</bdi> × <bdi dir="ltr">{formatMoneyValue(model.unitPricePiastres, locale, 'exact')}</bdi>
      </p>
      <p className="mt-1.5 flex items-baseline justify-between gap-2 border-y border-dashed border-paper-ink py-1">
        <span>{t('total')}</span>
        <span className="text-[0.9375rem] font-bold">
          {model.billable ? <bdi>{formatMoney(model.amountPiastres, locale)}</bdi> : t('nonBillable')}
        </span>
      </p>
      <div className="mt-2 flex items-center justify-between gap-2">
        <span className="font-semibold">{CHECK_MARK} {t('success')}</span>
        <QrCode value={qrValue} size={52} label={t('scan')} />
      </div>
    </figure>
  );
}
