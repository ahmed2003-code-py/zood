import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { SelectField, TextField, toast } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { FUEL_TYPES, opsApi, type FuelType, type Station } from '../api';
import { FormSheet } from '../components';
import { optional, parsePounds } from '../input';

type ErrorKey = 'price' | 'start' | 'note';

const API_FIELD: Record<string, ErrorKey> = {
  pricePiastres: 'price',
  effectiveFrom: 'start',
  note: 'note',
};

const NATIONAL = '';

/** Schedules a litre price, nationally or for one station. A price is never edited afterwards. */
export function PriceSheet({
  stations,
  onClose,
  onSaved,
}: {
  stations: Station[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const [fuel, setFuel] = useState<FuelType>('GAS_92');
  const [place, setPlace] = useState(NATIONAL);
  const [price, setPrice] = useState('');
  const [start, setStart] = useState('');
  const [note, setNote] = useState('');
  const [errors, setErrors] = useState<Partial<Record<ErrorKey, string>>>({});
  const clear = (key: ErrorKey) => setErrors((previous) => ({ ...previous, [key]: undefined }));

  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={t('prices.schedule')}
      description={t('prices.scheduleHint')}
      submitLabel={t('prices.schedule')}
      onSubmit={async () => {
        const found: Partial<Record<ErrorKey, string>> = {};
        const piastres = parsePounds(price);
        if (piastres === null || piastres <= 0) found.price = t('prices.invalidPrice');
        const startDate = start ? new Date(start) : undefined;
        if (startDate && Number.isNaN(startDate.getTime())) found.start = t('prices.invalidStart');
        if (Object.keys(found).length > 0) {
          setErrors(found);
          return false;
        }
        await opsApi.prices.schedule({
          fuelType: fuel,
          stationId: place === NATIONAL ? undefined : place,
          pricePiastres: piastres ?? 0,
          effectiveFrom: startDate?.toISOString(),
          note: optional(note),
        });
        toast(t('prices.scheduled'));
        onSaved();
      }}
      onError={(error) => {
        const name = error.field ? API_FIELD[error.field] : undefined;
        if (name) setErrors((previous) => ({ ...previous, [name]: error.message }));
      }}
    >
      <SelectField<FuelType>
        label={t('prices.fuel')}
        value={fuel}
        onValueChange={setFuel}
        options={FUEL_TYPES.map((value) => ({ value, label: t(`enums.fuel.${value}`) }))}
      />
      <SelectField
        label={t('prices.place')}
        value={place}
        onValueChange={setPlace}
        options={[
          { value: NATIONAL, label: t('prices.national') },
          ...stations.map((station) => ({ value: station.id, label: locale === 'ar' ? station.nameAr : station.nameEn })),
        ]}
      />
      <TextField
        label={t('prices.price')}
        hint={t('prices.priceHint')}
        inputMode="decimal"
        dir="ltr"
        value={price}
        error={errors.price}
        onChange={(event) => {
          setPrice(event.target.value);
          clear('price');
        }}
      />
      <TextField
        label={t('common.optional', { label: t('prices.start') })}
        hint={t('prices.startHint')}
        type="datetime-local"
        dir="ltr"
        value={start}
        error={errors.start}
        onChange={(event) => {
          setStart(event.target.value);
          clear('start');
        }}
      />
      <TextField
        label={t('common.optional', { label: t('prices.note') })}
        maxLength={300}
        value={note}
        error={errors.note}
        onChange={(event) => {
          setNote(event.target.value);
          clear('note');
        }}
      />
    </FormSheet>
  );
}
