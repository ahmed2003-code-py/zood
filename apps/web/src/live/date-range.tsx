/**
 * A period filter shared by the lists, the statement and the reports: a quick period (this month,
 * last 30 days…) or two Cairo calendar days. A range that ends before it starts is refused on the
 * spot (QA round 1) and never sent to the API.
 */
import { useTranslation } from 'react-i18next';
import { SelectField, TextField } from '@/design-system';
import { QUICK_PERIODS } from '@/lib/periods';
import { isInvertedRange, type DateRangeState, type PeriodChoice } from './date-range-state';

/** The quick-period select and the two day fields, side by side also on a phone. */
export function DateRangeFields({ range, allowCustomOnly = false }: { range: DateRangeState; allowCustomOnly?: boolean }) {
  const { t } = useTranslation(['live', 'portal']);
  const inverted = isInvertedRange(range.from, range.to);
  return (
    <>
      {!allowCustomOnly && (
        <SelectField<PeriodChoice>
          className="md:w-48"
          label={t('periods.label')}
          value={range.period}
          onValueChange={range.setPeriod}
          options={[{ value: 'CUSTOM', label: t('periods.CUSTOM') }, ...QUICK_PERIODS.map((value) => ({ value, label: t(`periods.${value}`) }))]}
        />
      )}
      <div className="grid grid-cols-2 gap-3 md:flex">
        <TextField
          className="md:w-44"
          label={t('fuelings.from')}
          type="date"
          dir="ltr"
          value={range.from}
          aria-invalid={inverted || undefined}
          onChange={(event) => range.setFrom(event.target.value)}
        />
        <TextField
          className="md:w-44"
          label={t('fuelings.to')}
          type="date"
          dir="ltr"
          value={range.to}
          error={inverted ? t('portal:dateRange.invalid') : undefined}
          onChange={(event) => range.setTo(event.target.value)}
        />
      </div>
    </>
  );
}
