import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { EmptyState, Skeleton } from './feedback';
import { List } from './layout';

export interface DataColumn<T> {
  id: string;
  /** Translated header text. */
  header: string;
  cell: (row: T) => ReactNode;
  /** Numbers and amounts align to the inline end. */
  align?: 'start' | 'end';
  className?: string;
}

interface DataViewProps<T> {
  rows: T[];
  columns: DataColumn<T>[];
  getRowId: (row: T) => string;
  /** Mobile representation of a row — usually a <ListRow>. */
  renderListItem: (row: T) => ReactNode;
  onRowClick?: (row: T) => void;
  /** Accessible table caption (visually hidden). */
  caption: string;
  loading?: boolean;
  empty?: ReactNode;
  /** Breakpoint from which the table replaces the list. */
  tableFrom?: 'md' | 'lg';
  /** Extra classes per row (table row and list item). */
  rowClassName?: (row: T) => string | undefined;
}

const BREAKPOINT = {
  md: { table: 'hidden md:block', list: 'md:hidden' },
  lg: { table: 'hidden lg:block', list: 'lg:hidden' },
} as const;

/**
 * Responsive collection: a table on wide screens, a compact list on narrow screens.
 * Large tables are never squeezed or horizontally scrolled on mobile.
 */
export function DataView<T>({
  rows,
  columns,
  getRowId,
  renderListItem,
  onRowClick,
  caption,
  loading = false,
  empty,
  tableFrom = 'lg',
  rowClassName,
}: DataViewProps<T>) {
  if (loading) return <RowsSkeleton />;
  if (rows.length === 0) return <>{empty ?? <EmptyState />}</>;

  const classes = BREAKPOINT[tableFrom];
  return (
    <>
      <div className={classes.table}>
        <table className="w-full border-separate border-spacing-0 text-sm">
          <caption className="sr-only">{caption}</caption>
          <thead>
            <tr>
              {columns.map((column) => (
                <th
                  key={column.id}
                  scope="col"
                  className={cn(
                    // The header stays under the sticky page header (4rem) while a long list scrolls.
                    'sticky top-16 z-[1] h-10 border-b border-border bg-surface-muted px-4 text-label font-medium whitespace-nowrap text-muted first:ps-5 last:pe-5',
                    column.align === 'end' ? 'text-end' : 'text-start',
                  )}
                >
                  {column.header}
                </th>
              ))}
            </tr>
          </thead>
          <tbody className="[&>tr:last-child>td]:border-b-0">
            {rows.map((row) => (
              <tr
                key={getRowId(row)}
                tabIndex={onRowClick ? 0 : undefined}
                onClick={onRowClick ? () => onRowClick(row) : undefined}
                onKeyDown={
                  onRowClick
                    ? (event) => {
                        if (event.key === 'Enter' || event.key === ' ') {
                          event.preventDefault();
                          onRowClick(row);
                        }
                      }
                    : undefined
                }
                className={cn(
                  'group/row',
                  onRowClick && 'cursor-pointer focus-visible:outline-offset-[-2px]',
                  rowClassName?.(row),
                )}
              >
                {columns.map((column) => (
                  <td
                    key={column.id}
                    className={cn(
                      'h-13 border-b border-border px-4 py-2.5 align-middle first:ps-5 last:pe-5',
                      onRowClick && 'transition-colors duration-150 group-hover/row:bg-surface-hover',
                      column.align === 'end' ? 'text-end whitespace-nowrap tabular-nums' : 'text-start',
                      column.className,
                    )}
                  >
                    {column.cell(row)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <List className={classes.list}>
        {rows.map((row) => (
          <div key={getRowId(row)} className={rowClassName?.(row)}>
            {renderListItem(row)}
          </div>
        ))}
      </List>
    </>
  );
}

/** Placeholder rows shaped like the list that is loading, so the screen does not jump. */
export function RowsSkeleton({ rows = 5 }: { rows?: number }) {
  return (
    <div aria-busy className="divide-y divide-border">
      {Array.from({ length: rows }, (_, index) => (
        <div key={index} className="flex items-center gap-4 px-4 py-4 md:px-5">
          <Skeleton className="size-9 shrink-0 rounded-full" />
          <div className="flex min-w-0 flex-1 flex-col gap-2">
            <Skeleton className={cn('h-3.5', index % 2 ? 'w-2/5' : 'w-1/3')} />
            <Skeleton className="h-3 w-1/4" />
          </div>
          <Skeleton className="h-3.5 w-20" />
        </div>
      ))}
    </div>
  );
}
