import { SlidersHorizontal } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { FuelTransaction } from '@/domain/types';
import { Button, SegmentedControl, Sheet } from '@/design-system';
import type { PeriodFilter } from '@/mocks/selectors';
import { DEFAULT_FILTERS, type FilterValues } from './filters';


interface Option {
  value: string;
  label: string;
}

interface TransactionFiltersProps {
  value: FilterValues;
  onChange: (value: FilterValues) => void;
  vehicles?: Option[];
  stations?: Option[];
}

const STATUSES: Array<FuelTransaction['status']> = ['completed', 'declined', 'pendingSync', 'reversed'];

function Select({ label, value, options, onChange }: { label: string; value?: string; options: Option[]; onChange: (value?: string) => void }) {
  const { t } = useTranslation('transactions');
  return (
    <label className="flex min-w-0 flex-col gap-1.5">
      <span className="text-xs font-medium text-muted">{label}</span>
      <select
        value={value ?? ''}
        onChange={(event) => onChange(event.target.value || undefined)}
        className="min-h-11 w-full rounded-control border border-input-border bg-input px-3 text-sm"
      >
        <option value="">{t('filters.any')}</option>
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    </label>
  );
}

function Fields({ value, onChange, vehicles, stations, stacked }: TransactionFiltersProps & { stacked?: boolean }) {
  const { t } = useTranslation(['transactions', 'status']);
  const wrap = (children: ReactNode) => (stacked ? <div className="flex flex-col gap-4">{children}</div> : <>{children}</>);
  return wrap(
    <>
      <div className="flex flex-col gap-1.5">
        <span className="text-xs font-medium text-muted">{t('filters.period')}</span>
        <SegmentedControl<PeriodFilter>
          label={t('filters.period')}
          value={value.period}
          onValueChange={(period) => onChange({ ...value, period })}
          options={(['today', 'week', 'month', 'all'] as const).map((period) => ({ value: period, label: t(`filters.periods.${period}`) }))}
          className={stacked ? 'w-full' : undefined}
        />
      </div>
      <Select
        label={t('filters.status')}
        value={value.status}
        onChange={(status) => onChange({ ...value, status: status as FilterValues['status'] })}
        options={STATUSES.map((status) => ({ value: status, label: t(`status:transaction.${status}`) }))}
      />
      {vehicles && <Select label={t('filters.vehicle')} value={value.vehicleId} onChange={(vehicleId) => onChange({ ...value, vehicleId })} options={vehicles} />}
      {stations && <Select label={t('filters.station')} value={value.stationId} onChange={(stationId) => onChange({ ...value, stationId })} options={stations} />}
    </>,
  );
}

/** Filters: inline on desktop, a bottom sheet on mobile. */
export function TransactionFilters(props: TransactionFiltersProps) {
  const { t } = useTranslation('transactions');
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState(props.value);
  return (
    <>
      <div className="mb-4 hidden flex-wrap items-end gap-3 lg:flex [&>label]:w-48">
        <Fields {...props} />
      </div>
      <div className="mb-4 lg:hidden">
        <Sheet
          open={open}
          onOpenChange={(next) => {
            if (next) setDraft(props.value);
            setOpen(next);
          }}
          title={t('filters.title')}
          trigger={
            <Button variant="secondary" leadingIcon={SlidersHorizontal}>
              {t('filters.open')}
            </Button>
          }
          footer={
            <>
              <Button variant="ghost" onClick={() => setDraft(DEFAULT_FILTERS)}>
                {t('filters.reset')}
              </Button>
              <Button
                onClick={() => {
                  props.onChange(draft);
                  setOpen(false);
                }}
              >
                {t('filters.apply')}
              </Button>
            </>
          }
        >
          <Fields {...props} value={draft} onChange={setDraft} stacked />
        </Sheet>
      </div>
    </>
  );
}
