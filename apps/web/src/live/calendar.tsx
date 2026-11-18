/**
 * The activity calendar (plan §7 B6): a month at a glance for whatever the user looks after (the
 * platform, a company, a vehicle, a station, a device, an agent). Each day shows its fuelings,
 * amount and alerts; choosing a day opens that day's fuelings. Weeks start on Saturday, like the
 * fueling limits; days are Cairo days.
 */
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { ErrorState, IconButton, Money, Skeleton } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { cairoDay } from '@/lib/periods';
import type { ActivityDay } from './api';
import { useResource } from './use-resource';

/** The first and last day of a month, as YYYY-MM-DD. */
function monthBounds(month: string): { from: string; to: string } {
  const [year, m] = month.split('-').map(Number) as [number, number];
  const last = new Date(Date.UTC(year, m, 0)).getUTCDate();
  return { from: `${month}-01`, to: `${month}-${String(last).padStart(2, '0')}` };
}

function shiftMonth(month: string, by: number): string {
  const [year, m] = month.split('-').map(Number) as [number, number];
  return new Date(Date.UTC(year, m - 1 + by, 1)).toISOString().slice(0, 7);
}

/** Blank cells before the 1st so it falls under its weekday, Saturday first. */
function leadingBlanks(day: string): number {
  const [year, m, d] = day.split('-').map(Number) as [number, number, number];
  return (new Date(Date.UTC(year, m - 1, d)).getUTCDay() + 1) % 7;
}

const WEEKDAYS = ['sat', 'sun', 'mon', 'tue', 'wed', 'thu', 'fri'] as const;

export function ActivityCalendar({
  load,
  onOpenDay,
}: {
  load: (from: string, to: string, signal: AbortSignal) => Promise<ActivityDay[]>;
  onOpenDay: (day: string) => void;
}) {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const today = cairoDay(new Date());
  const [month, setMonth] = useState(today.slice(0, 7));
  const bounds = monthBounds(month);
  const days = useResource((signal) => load(bounds.from, bounds.to, signal), [month]);
  const title = new Date(`${month}-15T12:00:00Z`).toLocaleDateString(locale === 'ar' ? 'ar-EG-u-nu-latn' : 'en-GB', {
    month: 'long',
    year: 'numeric',
    timeZone: 'UTC',
  });
  const totals = (days.data ?? []).reduce(
    (sum, day) => ({ fuelings: sum.fuelings + day.completed, amount: sum.amount + day.amountPiastres, alerts: sum.alerts + day.alerts }),
    { fuelings: 0, amount: 0, alerts: 0 },
  );

  return (
    <section className="flex flex-col gap-4" aria-label={t('calendar.title')}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-2">
          <IconButton icon={ChevronLeft} directional label={t('calendar.previous')} onClick={() => setMonth(shiftMonth(month, -1))} />
          <h2 className="min-w-40 text-center text-lg font-semibold">{title}</h2>
          <IconButton
            icon={ChevronRight}
            directional
            label={t('calendar.next')}
            disabled={month >= today.slice(0, 7)}
            onClick={() => setMonth(shiftMonth(month, 1))}
          />
        </div>
        {days.data && (
          <p className="text-sm text-muted">
            {t('calendar.totals', { fuelings: totals.fuelings, alerts: totals.alerts })} · <Money value={totals.amount} />
          </p>
        )}
      </div>

      {days.loading && <Skeleton className="h-96 w-full" />}
      {days.error ? <ErrorState onRetry={days.reload} /> : null}
      {days.data && (
        <div className="grid grid-cols-7 gap-1 sm:gap-2">
          {WEEKDAYS.map((weekday) => (
            <div key={weekday} className="pb-1 text-center text-xs font-medium text-muted">
              {t(`calendar.weekdays.${weekday}`)}
            </div>
          ))}
          {Array.from({ length: leadingBlanks(bounds.from) }, (_, index) => (
            <div key={`blank-${index}`} aria-hidden />
          ))}
          {days.data.map((day) => {
            const busy = day.completed + day.declined > 0;
            const future = day.day > today;
            return (
              <button
                key={day.day}
                type="button"
                disabled={future || !busy}
                onClick={() => onOpenDay(day.day)}
                aria-label={t('calendar.dayLabel', { day: day.day, fuelings: day.completed, declined: day.declined, alerts: day.alerts })}
                className={cn(
                  'flex min-h-16 flex-col items-start gap-0.5 rounded-control border p-1.5 text-start text-xs sm:min-h-24 sm:p-2',
                  busy ? 'border-border bg-surface hover:bg-surface-sunken' : 'border-transparent bg-surface-sunken text-muted',
                  day.day === today && 'border-primary',
                )}
              >
                <span className="text-sm font-semibold tabular-nums">{Number(day.day.slice(8))}</span>
                {busy && (
                  <>
                    <span className="tabular-nums">{t('calendar.fuelings', { count: day.completed })}</span>
                    <span className="hidden font-medium sm:inline">
                      <Money value={day.amountPiastres} />
                    </span>
                    <span className="flex flex-wrap gap-1">
                      {day.declined > 0 && <span className="rounded-full bg-danger-subtle px-1.5 text-danger">{day.declined}</span>}
                      {day.alerts > 0 && <span className="rounded-full bg-warning-subtle px-1.5 text-warning">{day.alerts}</span>}
                      {day.offline > 0 && <span className="rounded-full bg-info-subtle px-1.5 text-info">{day.offline}</span>}
                    </span>
                  </>
                )}
              </button>
            );
          })}
        </div>
      )}
      <p className="flex flex-wrap gap-3 text-xs text-muted">
        <span className="flex items-center gap-1">
          <span className="size-2.5 rounded-full bg-danger" aria-hidden /> {t('calendar.legend.declined')}
        </span>
        <span className="flex items-center gap-1">
          <span className="size-2.5 rounded-full bg-warning" aria-hidden /> {t('calendar.legend.alerts')}
        </span>
        <span className="flex items-center gap-1">
          <span className="size-2.5 rounded-full bg-info" aria-hidden /> {t('calendar.legend.offline')}
        </span>
      </p>
    </section>
  );
}
