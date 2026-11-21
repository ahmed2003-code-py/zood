/**
 * Building blocks of the station portal: dates with the year, a validated date range with the
 * chosen days spelled out in the reader's language, filters that fold into a sheet on phones, the
 * station picker, and device and agent health chips. Station screens only.
 */
import { useState, useSyncExternalStore, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { CircleAlert, CircleCheck, CircleDashed, CircleOff, Clock, KeyRound, Lock, SlidersHorizontal, WifiOff } from 'lucide-react';
import { Button, FilterBar, SelectField, Sheet, StatusChip, TextField, type Tone } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatDate, formatDateTime, formatTime } from '@/lib/format';
import { periodRange, QUICK_PERIODS, type QuickPeriod } from '@/lib/periods';
import type { Station } from '../api';
import { useValidRange, type DayRangeValue } from './hooks';
import type { AgentHealth, DeviceHealth } from './station-model';

// ------------------------------------------------------------------------------------- dates

/** A date and time with the year, in the reader's language, Cairo time. */
export function Stamp({ value, variant = 'dateTime', className }: { value: string; variant?: 'dateTime' | 'date' | 'time'; className?: string }) {
  const { locale } = useLocale();
  const text =
    variant === 'date' ? formatDate(value, locale, { withYear: true }) : variant === 'time' ? formatTime(value, locale) : formatDateTime(value, locale, { withYear: true });
  return (
    <time dateTime={value} className={cn('tabular-nums whitespace-nowrap', className)}>
      {text}
    </time>
  );
}

/** A calendar day (YYYY-MM-DD, Cairo) as a date with the year; the day's noon avoids any zone edge. */
export function DayText({ day, className }: { day: string; className?: string }) {
  return <Stamp value={`${day}T10:00:00Z`} variant="date" className={className} />;
}


/**
 * Two date fields with quick periods. The browser's date control shows its own format, so the
 * chosen day is also written out under it in the reader's language, with the year. A range that
 * ends before it starts, lies in the future or spans more than a year is explained, not sent.
 */
export function DayRangeFields({ value, onChange }: { value: DayRangeValue; onChange: (next: DayRangeValue) => void }) {
  const { t } = useTranslation(['station', 'live']);
  const { locale } = useLocale();
  const [period, setPeriod] = useState<'CUSTOM' | QuickPeriod>('CUSTOM');
  const { problem, today } = useValidRange(value);
  const hint = (day: string) => (/^\d{4}-\d{2}-\d{2}$/.test(day) ? formatDate(`${day}T10:00:00Z`, locale, { withYear: true }) : undefined);
  return (
    <>
      <SelectField<'CUSTOM' | QuickPeriod>
        className="md:w-48"
        label={t('live.range.period')}
        value={period}
        onValueChange={(next) => {
          setPeriod(next);
          onChange(next === 'CUSTOM' ? { from: '', to: '' } : periodRange(next));
        }}
        options={[{ value: 'CUSTOM', label: t('live.range.custom') }, ...QUICK_PERIODS.map((item) => ({ value: item, label: t(`live:periods.${item}`) }))]}
      />
      <div className="grid grid-cols-2 gap-3 md:flex md:items-start">
        <TextField
          className="md:w-44"
          label={t('live.range.from')}
          type="date"
          lang={locale}
          max={value.to || today}
          value={value.from}
          hint={hint(value.from)}
          error={problem === 'FROM_AFTER_TO' ? t('live.range.problems.FROM_AFTER_TO') : undefined}
          onChange={(event) => {
            setPeriod('CUSTOM');
            onChange({ ...value, from: event.target.value });
          }}
        />
        <TextField
          className="md:w-44"
          label={t('live.range.to')}
          type="date"
          lang={locale}
          min={value.from || undefined}
          max={today}
          value={value.to}
          hint={hint(value.to)}
          error={problem && problem !== 'FROM_AFTER_TO' ? t(`live.range.problems.${problem}`) : undefined}
          onChange={(event) => {
            setPeriod('CUSTOM');
            onChange({ ...value, to: event.target.value });
          }}
        />
      </div>
    </>
  );
}

// ----------------------------------------------------------------------------------- filters

const WIDE = '(min-width: 768px)';

function useWide(): boolean {
  return useSyncExternalStore(
    (notify) => {
      const media = window.matchMedia(WIDE);
      media.addEventListener('change', notify);
      return () => media.removeEventListener('change', notify);
    },
    () => window.matchMedia(WIDE).matches,
    () => true,
  );
}

/**
 * A list's filters: the toolbar of the list from 768 px; on a phone one "Filters" button with the
 * number in use, opening the same fields in a bottom sheet.
 */
export function ListFilters({ active, onClear, children }: { active: number; onClear: () => void; children: ReactNode }) {
  const { t } = useTranslation('station');
  const wide = useWide();
  const [open, setOpen] = useState(false);
  if (wide) return <FilterBar>{children}</FilterBar>;
  return (
    <div className="flex items-center gap-2 border-b border-border bg-surface-muted px-4 py-3">
      <Button variant="secondary" leadingIcon={SlidersHorizontal} onClick={() => setOpen(true)}>
        {active > 0 ? t('live.filters.buttonActive', { count: active }) : t('live.filters.button')}
      </Button>
      {active > 0 && (
        <Button variant="ghost" onClick={onClear}>
          {t('live.filters.clear')}
        </Button>
      )}
      <Sheet
        open={open}
        onOpenChange={setOpen}
        title={t('live.filters.title')}
        footer={
          <>
            <Button variant="secondary" onClick={onClear} disabled={active === 0}>
              {t('live.filters.clear')}
            </Button>
            <Button onClick={() => setOpen(false)}>{t('live.filters.show')}</Button>
          </>
        }
      >
        <div className="flex flex-col gap-5">{children}</div>
      </Sheet>
    </div>
  );
}

/** "All stations" plus each of the operator's stations, by name in the reader's language. */
export function StationSelect({ stations, value, onChange, className }: { stations: readonly Station[]; value: string; onChange: (id: string) => void; className?: string }) {
  const { t } = useTranslation('station');
  const { locale } = useLocale();
  return (
    <SelectField
      className={className ?? 'md:w-56'}
      label={t('live.filters.station')}
      value={value || 'ALL'}
      onValueChange={(next) => onChange(next === 'ALL' ? '' : next)}
      options={[{ value: 'ALL', label: t('live.filters.allStations') }, ...stations.map((station) => ({ value: station.id, label: locale === 'ar' ? station.nameAr : station.nameEn }))]}
    />
  );
}

// ------------------------------------------------------------------------------------ health

const DEVICE_HEALTH: Record<DeviceHealth, { tone: Tone; icon: typeof CircleCheck }> = {
  ONLINE: { tone: 'success', icon: CircleCheck },
  OFFLINE: { tone: 'warning', icon: WifiOff },
  NEVER_SYNCED: { tone: 'neutral', icon: CircleDashed },
  AWAITING_ACTIVATION: { tone: 'info', icon: Clock },
  CODE_EXPIRED: { tone: 'danger', icon: CircleAlert },
  REVOKED: { tone: 'neutral', icon: CircleOff },
};

export function DeviceHealthChip({ health }: { health: DeviceHealth }) {
  const { t } = useTranslation('station');
  const { tone, icon } = DEVICE_HEALTH[health];
  return <StatusChip tone={tone} icon={icon} label={t(`live.deviceHealth.${health}`)} />;
}

const AGENT_HEALTH: Record<AgentHealth, { tone: Tone; icon: typeof CircleCheck }> = {
  ACTIVE: { tone: 'success', icon: CircleCheck },
  LOCKED: { tone: 'danger', icon: Lock },
  MUST_CHANGE_PIN: { tone: 'warning', icon: KeyRound },
  SUSPENDED: { tone: 'warning', icon: CircleOff },
  DEACTIVATED: { tone: 'neutral', icon: CircleOff },
};

export function AgentHealthChip({ health, lockedUntil }: { health: AgentHealth; lockedUntil?: string }) {
  const { t } = useTranslation('station');
  const { locale } = useLocale();
  const { tone, icon } = AGENT_HEALTH[health];
  const label = health === 'LOCKED' && lockedUntil ? t('live.agentHealth.lockedUntil', { time: formatTime(lockedUntil, locale) }) : t(`live.agentHealth.${health}`);
  return <StatusChip tone={tone} icon={icon} label={label} />;
}
