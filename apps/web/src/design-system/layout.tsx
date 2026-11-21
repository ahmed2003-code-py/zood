import { ChevronRight } from 'lucide-react';
import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { Icon } from './Icon';

interface PanelProps {
  title?: string;
  /** One line under the title that says what the section is for. */
  description?: string;
  /** Usually a "View all" link or a small control, placed at the inline end of the header. */
  action?: ReactNode;
  children: ReactNode;
  /** flush: no inner padding (for lists and tables that draw their own rows). */
  flush?: boolean;
  className?: string;
}

/** A bordered surface section. Use one per topic; do not nest panels. */
export function Panel({ title, description, action, children, flush = false, className }: PanelProps) {
  return (
    <section className={cn('min-w-0 overflow-clip rounded-container border border-border bg-surface shadow-card', className)}>
      {(title || action) && (
        <header
          className={cn(
            'flex min-h-13 items-center justify-between gap-3 px-4 md:px-5',
            flush ? 'border-b border-border py-3' : 'pt-4 pb-1',
          )}
        >
          <div className="min-w-0">
            {title && <h2 className="text-base font-semibold text-balance">{title}</h2>}
            {description && <p className="mt-0.5 text-sm text-muted">{description}</p>}
          </div>
          {action}
        </header>
      )}
      <div className={cn(!flush && 'p-4 md:p-5')}>{children}</div>
    </section>
  );
}

interface KpiTileProps {
  label: string;
  value: ReactNode;
  hint?: ReactNode;
  /** The single most important figure on a screen gets the accent speed line before its label. */
  emphasis?: boolean;
  className?: string;
}

export function KpiTile({ label, value, hint, emphasis = false, className }: KpiTileProps) {
  return (
    <div className={cn('flex min-w-0 flex-col gap-1.5 rounded-container border border-border bg-surface p-4 shadow-card md:p-5', className)}>
      <span className="flex min-w-0 items-center gap-2 text-label font-medium text-muted">
        {emphasis && <span aria-hidden className="h-1 w-4 shrink-0 rounded-full bg-accent" />}
        <span className="truncate">{label}</span>
      </span>
      {/* Smaller on phones so an amount in a two-column row stays whole. */}
      <span className="truncate text-lg leading-tight font-semibold tracking-tight tabular-nums sm:text-2xl md:text-kpi">{value}</span>
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
        {subtitle && <span className="truncate text-label text-muted">{subtitle}</span>}
      </span>
      {(trailing || trailingSub) && (
        <span className="flex shrink-0 flex-col items-end gap-1 text-end">
          {trailing && <span className="font-semibold">{trailing}</span>}
          {trailingSub}
        </span>
      )}
      {showChevron && <Icon icon={ChevronRight} directional size={16} className="text-muted" />}
    </>
  );
  const base = 'flex min-h-14 w-full items-center gap-3 px-4 py-3 text-start';
  if (onClick) {
    return (
      <button type="button" onClick={onClick} className={cn(base, 'transition-colors duration-150 hover:bg-surface-hover', className)}>
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

/**
 * A record with its status and actions: what it is at the start, the status chip and buttons at the
 * inline end. Laid out by the width of its list, not the screen: in a narrow column the status and
 * buttons drop under the record.
 */
export function RecordRow({ children, status, actions }: { children: ReactNode; status?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="@container">
      <div className="flex flex-col gap-3 px-4 py-3.5 @md:px-5 @lg:flex-row @lg:items-center @lg:gap-6">
        <div className="min-w-0 flex-1">{children}</div>
        {(status || actions) && (
          <div className="flex shrink-0 flex-wrap items-center gap-2 @lg:justify-end">
            {status}
            {actions}
          </div>
        )}
      </div>
    </div>
  );
}
