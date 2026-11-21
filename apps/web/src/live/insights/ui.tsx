/** Small pieces the dashboards, budgets, limits and reports share. */
import { Info } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Icon, Skeleton } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatNumber } from '@/lib/format';

export type MeterState = 'ok' | 'near' | 'over' | 'none';

const FILL: Record<MeterState, string> = {
  ok: 'bg-primary',
  near: 'bg-warning',
  over: 'bg-danger',
  none: 'bg-border-strong',
};

/**
 * How much of a budget or limit is used: a bar, never colour alone (the caller writes the figures
 * and the state in words next to it). Past 100 % the bar is full and turns to the danger colour.
 */
export function UsageMeter({ percent, state, label, className }: { percent: number; state: MeterState; label: string; className?: string }) {
  const width = Math.max(0, Math.min(100, percent));
  return (
    <div
      role="meter"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.min(percent, 100)}
      className={cn('h-2 w-full overflow-hidden rounded-full bg-surface-sunken', className)}
    >
      <div className={cn('h-full rounded-full transition-[width] duration-500 ease-out', FILL[state])} style={{ width: `${width}%` }} />
    </div>
  );
}

/** The figures cover the newest rows only (the period has more than the page cap). */
export function PartialNote({ count }: { count: number }) {
  const { t } = useTranslation('portal');
  const { locale } = useLocale();
  return (
    <p className="flex items-start gap-2 text-xs text-muted">
      <Icon icon={Info} size={14} className="mt-0.5 shrink-0" />
      {t('insights.partial', { count: formatNumber(count, locale) })}
    </p>
  );
}

/** A labelled figure inside a card row: label on top, value under it, both start-aligned. */
export function Figure({ label, value, hint, className }: { label: string; value: ReactNode; hint?: ReactNode; className?: string }) {
  return (
    <div className={cn('flex min-w-0 flex-col gap-0.5', className)}>
      <span className="truncate text-xs text-muted">{label}</span>
      <span className="truncate text-sm font-semibold tabular-nums">{value}</span>
      {hint && <span className="truncate text-xs text-muted">{hint}</span>}
    </div>
  );
}

/** Rows shaped like the meter lists that are loading. */
export function MeterRowsSkeleton({ rows = 3 }: { rows?: number }) {
  return (
    <div aria-busy className="flex flex-col divide-y divide-border">
      {Array.from({ length: rows }, (_, index) => (
        <div key={index} className="flex flex-col gap-2.5 px-4 py-4 md:px-5">
          <div className="flex justify-between gap-4">
            <Skeleton className="h-3.5 w-1/3" />
            <Skeleton className="h-3.5 w-24" />
          </div>
          <Skeleton className="h-2 w-full rounded-full" />
        </div>
      ))}
    </div>
  );
}

/** KPI tiles that are loading, in the grid the loaded ones use. */
export function KpiRowSkeleton({ tiles = 4 }: { tiles?: number }) {
  return (
    <div aria-busy className="grid grid-cols-2 gap-3 md:gap-4 lg:grid-cols-4">
      {Array.from({ length: tiles }, (_, index) => (
        <div key={index} className="flex flex-col gap-2.5 rounded-container border border-border bg-surface p-4 shadow-card md:p-5">
          <Skeleton className="h-3 w-2/3" />
          <Skeleton className="h-7 w-1/2" />
          <Skeleton className="h-3 w-1/3" />
        </div>
      ))}
    </div>
  );
}
