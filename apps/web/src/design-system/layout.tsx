import { ChevronRight } from 'lucide-react';
import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { Icon } from './Icon';

interface PanelProps {
  title?: string;
  /** Usually a "View all" link or a small control, placed at the inline end of the header. */
  action?: ReactNode;
  children: ReactNode;
  /** flush: no inner padding (for lists and tables that draw their own rows). */
  flush?: boolean;
  className?: string;
}

/** A bordered surface section. Use one per topic; do not nest panels. */
export function Panel({ title, action, children, flush = false, className }: PanelProps) {
  return (
    <section className={cn('min-w-0 rounded-container border border-border bg-surface', className)}>
      {(title || action) && (
        <header className="flex min-h-12 items-center justify-between gap-3 border-b border-border px-4 py-2">
          {title && <h2 className="text-base font-semibold">{title}</h2>}
          {action}
        </header>
      )}
      <div className={cn(!flush && 'p-4')}>{children}</div>
    </section>
  );
}

interface KpiTileProps {
  label: string;
  value: ReactNode;
  hint?: ReactNode;
  /** The single most important figure on a screen gets the accent marker. */
  emphasis?: boolean;
  className?: string;
}

export function KpiTile({ label, value, hint, emphasis = false, className }: KpiTileProps) {
  return (
    <div
      className={cn(
        'flex min-w-0 flex-col gap-1 rounded-container border border-border bg-surface p-4 md:p-5',
        emphasis && 'border-s-4 border-s-accent',
        className,
      )}
    >
      <span className="truncate text-sm text-muted">{label}</span>
      <span className="truncate text-2xl leading-tight font-semibold tabular-nums md:text-[1.75rem]">{value}</span>
      {hint && <span className="truncate text-xs text-muted">{hint}</span>}
    </div>
  );
}

interface ListRowProps {
  leading?: ReactNode;
  title: ReactNode;
  subtitle?: ReactNode;
  trailing?: ReactNode;
  trailingSub?: ReactNode;
  onClick?: () => void;
  /** Shows a forward chevron (mirrored in RTL). Defaults to true when the row is clickable. */
  chevron?: boolean;
  className?: string;
}

/** Compact list item: the mobile representation of a table row. */
export function ListRow({ leading, title, subtitle, trailing, trailingSub, onClick, chevron, className }: ListRowProps) {
  const showChevron = chevron ?? Boolean(onClick);
  const content = (
    <>
      {leading && <span className="shrink-0">{leading}</span>}
      <span className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span className="truncate font-medium">{title}</span>
        {subtitle && <span className="truncate text-sm text-muted">{subtitle}</span>}
      </span>
      {(trailing || trailingSub) && (
        <span className="flex shrink-0 flex-col items-end gap-1 text-end">
          {trailing && <span className="font-semibold">{trailing}</span>}
          {trailingSub}
        </span>
      )}
      {showChevron && <Icon icon={ChevronRight} directional size={18} className="text-muted" />}
    </>
  );
  const base = 'flex min-h-14 w-full items-center gap-3 px-4 py-3 text-start';
  if (onClick) {
    return (
      <button type="button" onClick={onClick} className={cn(base, 'transition-colors hover:bg-surface-sunken', className)}>
        {content}
      </button>
    );
  }
  return <div className={cn(base, className)}>{content}</div>;
}

/** Vertical list with dividers between rows (no card per row). */
export function List({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn('divide-y divide-border', className)}>{children}</div>;
}
