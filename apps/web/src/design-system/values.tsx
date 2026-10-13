/**
 * Value renderers. Each value is wrapped in <bdi> with an explicit direction so it never gets
 * reordered by the surrounding text (an Arabic plate inside English text, a signed amount inside
 * Arabic text, etc.).
 */
import { useTranslation } from 'react-i18next';
import type { Milliliters, Piastres } from '@/domain/money';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import {
  formatDate,
  formatDateTime,
  formatMoney,
  formatRelative,
  formatTime,
  formatVolume,
  type MoneyFormatOptions,
} from '@/lib/format';

interface MoneyProps extends MoneyFormatOptions {
  value: Piastres;
  className?: string;
}

export function Money({ value, precision, signDisplay, className }: MoneyProps) {
  const { locale, dir } = useLocale();
  return (
    <bdi dir={dir} className={cn('tabular-nums whitespace-nowrap', className)}>
      {formatMoney(value, locale, { precision, signDisplay })}
    </bdi>
  );
}

interface VolumeProps {
  value: Milliliters;
  fractionDigits?: number;
  className?: string;
}

export function Volume({ value, fractionDigits, className }: VolumeProps) {
  const { locale, dir } = useLocale();
  return (
    <bdi dir={dir} className={cn('tabular-nums whitespace-nowrap', className)}>
      {formatVolume(value, locale, fractionDigits)}
    </bdi>
  );
}

const PLATE_SIZE = {
  sm: 'text-xs px-1.5 pb-0.5 pt-1',
  md: 'text-sm px-2 pb-0.5 pt-1.5',
  lg: 'text-2xl px-4 pb-1.5 pt-3',
} as const;

interface PlateProps {
  /** Exactly as registered. Plates are official identifiers and are never transliterated. */
  value: string;
  size?: keyof typeof PLATE_SIZE;
  /** framed: plate-like box. plain: text only (for dense rows). */
  variant?: 'framed' | 'plain';
  className?: string;
}

export function Plate({ value, size = 'md', variant = 'framed', className }: PlateProps) {
  const { t } = useTranslation();
  if (variant === 'plain') {
    return (
      <bdi dir="rtl" lang="ar" className={cn('font-semibold whitespace-nowrap', className)}>
        {value}
      </bdi>
    );
  }
  return (
    <bdi
      dir="rtl"
      lang="ar"
      role="img"
      aria-label={`${t('a11y.licensePlate')} ${value}`}
      className={cn(
        'relative inline-flex items-center overflow-hidden rounded-md border border-border-strong bg-surface',
        'font-semibold whitespace-nowrap text-foreground [word-spacing:0.15em]',
        PLATE_SIZE[size],
        className,
      )}
    >
      <span aria-hidden className={cn('absolute inset-x-0 top-0 bg-info', size === 'lg' ? 'h-1.5' : 'h-1')} />
      {value}
    </bdi>
  );
}

type DateTimeVariant = 'dateTime' | 'date' | 'time' | 'relative';

interface DateTimeTextProps {
  value: string;
  variant?: DateTimeVariant;
  timeZone?: string;
  className?: string;
}

export function DateTimeText({ value, variant = 'dateTime', timeZone, className }: DateTimeTextProps) {
  const { locale } = useLocale();
  const text =
    variant === 'date'
      ? formatDate(value, locale, { timeZone })
      : variant === 'time'
        ? formatTime(value, locale, timeZone)
        : variant === 'relative'
          ? formatRelative(value, locale)
          : formatDateTime(value, locale, { timeZone });
  return (
    <time dateTime={value} className={cn('tabular-nums whitespace-nowrap', className)}>
      {text}
    </time>
  );
}
