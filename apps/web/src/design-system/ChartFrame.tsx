import { useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { useTheme } from '@/theme/theme-context';

/**
 * Chart colors resolved from the current theme's tokens. Charts never hardcode colors, so they
 * follow Light/Dark automatically.
 */
export interface ChartTheme {
  series: [string, string, string, string];
  grid: string;
  axis: string;
  surface: string;
}

const readTheme = (element: HTMLElement): ChartTheme => {
  const style = getComputedStyle(element);
  const token = (name: string) => style.getPropertyValue(name).trim();
  return {
    series: [token('--chart-1'), token('--chart-2'), token('--chart-3'), token('--chart-4')],
    grid: token('--chart-grid'),
    axis: token('--chart-axis'),
    surface: token('--surface'),
  };
};

export interface ChartLegendItem {
  label: string;
  /** Index into ChartTheme.series. */
  series: 0 | 1 | 2 | 3;
}

interface ChartFrameProps {
  title: string;
  description?: string;
  /** Small controls (e.g. a period selector), placed at the inline end of the header. */
  actions?: ReactNode;
  legend?: ChartLegendItem[];
  height?: number;
  children: (theme: ChartTheme) => ReactNode;
  className?: string;
}

/**
 * Chart container.
 *
 * IMPORTANT — chart direction (approved product decision):
 * The plotting area is ALWAYS rendered left-to-right, in Arabic and in English. Time series run
 * oldest → newest, left → right in both languages. Do NOT mirror chart data, axes or the time
 * scale for RTL. Only the surrounding UI (title, legend, actions) follows the page direction.
 * Tooltips render their own content in the page direction.
 */
export function ChartFrame({ title, description, actions, legend, height = 220, children, className }: ChartFrameProps) {
  const ref = useRef<HTMLDivElement>(null);
  const { resolved } = useTheme();
  const [theme, setTheme] = useState<ChartTheme | null>(null);

  // Re-read token values whenever the theme changes (before paint, to avoid a color flash).
  useLayoutEffect(() => {
    if (ref.current) setTheme(readTheme(ref.current));
  }, [resolved]);

  return (
    <section ref={ref} className={cn('min-w-0 rounded-container border border-border bg-surface p-4', className)}>
      <header className="mb-3 flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <h2 className="text-base font-semibold">{title}</h2>
          {description && <p className="text-xs text-muted">{description}</p>}
        </div>
        {actions}
      </header>
      {legend && theme && (
        <ul className="mb-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted">
          {legend.map((item) => (
            <li key={item.label} className="flex items-center gap-1.5">
              <span aria-hidden className="size-2.5 rounded-sm" style={{ backgroundColor: theme.series[item.series] }} />
              {item.label}
            </li>
          ))}
        </ul>
      )}
      {/* Plotting area: always LTR (see the note above). */}
      <div dir="ltr" style={{ height }} className="w-full">
        {theme && children(theme)}
      </div>
    </section>
  );
}

interface ChartTooltipProps {
  active?: boolean;
  label?: ReactNode;
  rows: Array<{ label: string; value: ReactNode; series: number }>;
  theme: ChartTheme;
  dir: 'rtl' | 'ltr';
}

/** Tooltip body for charts: follows the page direction even though the plot is LTR. */
export function ChartTooltip({ active, label, rows, theme, dir }: ChartTooltipProps) {
  if (!active || rows.length === 0) return null;
  return (
    <div dir={dir} className="rounded-control border border-border bg-surface-elevated px-3 py-2 text-sm shadow-elevated">
      {label && <p className="mb-1 text-xs text-muted">{label}</p>}
      {rows.map((row) => (
        <p key={row.label} className="flex items-center gap-2">
          <span aria-hidden className="size-2 rounded-full" style={{ backgroundColor: theme.series[row.series] }} />
          <span className="text-muted">{row.label}</span>
          <span className="font-semibold">{row.value}</span>
        </p>
      ))}
    </div>
  );
}
