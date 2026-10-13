import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import type { Piastres } from '@/domain/money';
import { ChartFrame, ChartTooltip } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatDate, formatMoney, formatNumber, piastresToWholePounds } from '@/lib/format';

export interface SpendPoint {
  at: Date;
  amountPiastres: Piastres;
}

/**
 * Daily spend bars. Lazily loaded (Recharts is the largest dependency) so screens render first.
 * Plotting is always left-to-right (see ChartFrame).
 */
export default function SpendChart({ title, seriesLabel, points, height = 200 }: { title: string; seriesLabel: string; points: SpendPoint[]; height?: number }) {
  const { locale, dir } = useLocale();
  const chartData = points.map((point) => ({
    tick: formatDate(point.at, locale, { month: 'short' }),
    label: formatDate(point.at, locale, { month: 'long' }),
    pounds: piastresToWholePounds(point.amountPiastres),
    amountPiastres: point.amountPiastres,
  }));
  return (
    <ChartFrame title={title} height={height}>
      {(theme) => (
        <ResponsiveContainer width="100%" height="100%">
          <BarChart data={chartData} margin={{ top: 4, right: 4, bottom: 0, left: 4 }}>
            <CartesianGrid vertical={false} stroke={theme.grid} />
            <XAxis dataKey="tick" tickLine={false} axisLine={{ stroke: theme.grid }} tick={{ fill: theme.axis, fontSize: 11 }} interval="preserveStartEnd" minTickGap={16} />
            <YAxis width={52} tickLine={false} axisLine={false} tick={{ fill: theme.axis, fontSize: 11 }} tickFormatter={(value: number) => formatNumber(value, locale)} />
            <Tooltip
              cursor={{ fill: theme.grid, opacity: 0.5 }}
              content={({ active, payload }) => {
                const point = payload?.[0]?.payload as (typeof chartData)[number] | undefined;
                return (
                  <ChartTooltip
                    active={active}
                    dir={dir}
                    theme={theme}
                    label={point?.label}
                    rows={point ? [{ label: seriesLabel, value: formatMoney(point.amountPiastres, locale, { precision: 'whole' }), series: 0 }] : []}
                  />
                );
              }}
            />
            <Bar dataKey="pounds" fill={theme.series[0]} radius={[4, 4, 0, 0]} maxBarSize={28} />
          </BarChart>
        </ResponsiveContainer>
      )}
    </ChartFrame>
  );
}
