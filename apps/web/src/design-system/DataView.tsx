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
  if (loading) {
    return (
      <div className="flex flex-col gap-3 p-4" aria-busy>
        {Array.from({ length: 4 }, (_, index) => (
          <Skeleton key={index} className="h-10" />
        ))}
      </div>
    );
  }
  if (rows.length === 0) return <>{empty ?? <EmptyState />}</>;

  const classes = BREAKPOINT[tableFrom];
  return (
    <>
      <div className={classes.table}>
        <table className="w-full border-collapse text-sm">
          <caption className="sr-only">{caption}</caption>
          <thead>
            <tr className="border-b border-border">
              {columns.map((column) => (
                <th
                  key={column.id}
                  scope="col"
                  className={cn(
                    'px-4 py-2.5 text-xs font-medium whitespace-nowrap text-muted',
                    column.align === 'end' ? 'text-end' : 'text-start',
                  )}
                >
                  {column.header}
                </th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-border">
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
                className={cn(onRowClick && 'cursor-pointer transition-colors hover:bg-surface-sunken', rowClassName?.(row))}
              >
                {columns.map((column) => (
                  <td
                    key={column.id}
                    className={cn(
                      'px-4 py-3 align-middle',
                      column.align === 'end' ? 'text-end tabular-nums' : 'text-start',
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
