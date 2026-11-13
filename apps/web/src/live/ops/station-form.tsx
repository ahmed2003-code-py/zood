import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { SegmentedControl, SelectField, TextField, toast } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { FUEL_TYPES, opsApi, type FuelType, type Station, type StationBody } from '../api';
import { FormSheet } from '../components';
import { normalizeDigits, optional } from '../input';
import { useResource } from '../use-resource';

type Text = 'nameAr' | 'nameEn' | 'address' | 'latitude' | 'longitude' | 'timeZone';
type Connectivity = StationBody['connectivity'];
type ErrorKey = Text | 'operator' | 'fuelTypes';

const API_FIELD: Record<string, ErrorKey> = {
  nameAr: 'nameAr',
  nameEn: 'nameEn',
  address: 'address',
  latitude: 'latitude',
  longitude: 'longitude',
  timeZone: 'timeZone',
  fuelTypes: 'fuelTypes',
  operatorId: 'operator',
  'station.nameAr': 'nameAr',
  'station.nameEn': 'nameEn',
  'station.latitude': 'latitude',
  'station.longitude': 'longitude',
  'station.timeZone': 'timeZone',
  'station.fuelTypes': 'fuelTypes',
};

/** A coordinate is plain decimal text; null when it is not one or is out of range. */
function parseCoordinate(input: string, limit: number): number | null {
  const value = normalizeDigits(input.trim());
  if (!/^-?\d+(\.\d+)?$/.test(value)) return null;
  const number = Number(value);
  return Math.abs(number) <= limit ? number : null;
}

/**
 * Adding a station (no `station`) or editing one. When `operatorId` is given the operator is fixed
 * and not shown; otherwise the operator is chosen from the list. The operator never changes on edit.
 */
export function StationFormSheet({
  open,
  onOpenChange,
  operatorId,
  station,
  onSaved,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  operatorId?: string;
  station?: Station;
  onSaved: (station: Station) => void;
}) {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const editing = Boolean(station);
  const chooseOperator = !editing && !operatorId;
  const operators = useResource(
    (signal) => (chooseOperator ? opsApi.operators.list({ limit: 100 }, signal) : Promise.resolve({ items: [] })),
    [chooseOperator],
  );
  const [text, setText] = useState<Record<Text, string>>(() => ({
    nameAr: station?.nameAr ?? '',
    nameEn: station?.nameEn ?? '',
    address: station?.address ?? '',
    latitude: station?.latitude != null ? String(station.latitude) : '',
    longitude: station?.longitude != null ? String(station.longitude) : '',
    timeZone: station?.timeZone ?? '',
  }));
  const [operator, setOperator] = useState('');
  const [connectivity, setConnectivity] = useState<Connectivity>(station?.connectivity ?? 'NORMAL');
  const [fuelTypes, setFuelTypes] = useState<FuelType[]>(station?.fuelTypes ?? []);
  const [errors, setErrors] = useState<Partial<Record<ErrorKey, string>>>({});

  const field = (name: Text) => ({
    value: text[name],
    error: errors[name],
    onChange: (event: { target: { value: string } }) => {
      setText((previous) => ({ ...previous, [name]: event.target.value }));
      setErrors((previous) => ({ ...previous, [name]: undefined }));
    },
  });

  const toggleFuel = (fuel: FuelType) => {
    setFuelTypes((previous) => (previous.includes(fuel) ? previous.filter((item) => item !== fuel) : [...previous, fuel]));
    setErrors((previous) => ({ ...previous, fuelTypes: undefined }));
  };

  const submit = async () => {
    const found: Partial<Record<ErrorKey, string>> = {};
    for (const name of ['nameAr', 'nameEn'] as const) {
      if (!text[name].trim()) found[name] = t('errors.required');
    }
    if (chooseOperator && !operator) found.operator = t('errors.required');
    const hasLat = Boolean(text.latitude.trim());
    const hasLng = Boolean(text.longitude.trim());
    const latitude = hasLat ? parseCoordinate(text.latitude, 90) : undefined;
    const longitude = hasLng ? parseCoordinate(text.longitude, 180) : undefined;
    if (latitude === null) found.latitude = t('stationForm.invalidCoordinate', { max: 90 });
    if (longitude === null) found.longitude = t('stationForm.invalidCoordinate', { max: 180 });
    if (hasLat !== hasLng) found[hasLat ? 'longitude' : 'latitude'] = t('errors.INCOMPLETE_LOCATION');
    if (fuelTypes.length === 0) found.fuelTypes = t('stationForm.fuelTypesRequired');
    if (Object.keys(found).length > 0) {
      setErrors(found);
      return false;
    }
    const body: StationBody = {
      nameAr: text.nameAr.trim(),
      nameEn: text.nameEn.trim(),
      address: optional(text.address),
      latitude: latitude ?? undefined,
      longitude: longitude ?? undefined,
      timeZone: optional(text.timeZone),
      connectivity,
      fuelTypes: FUEL_TYPES.filter((fuel) => fuelTypes.includes(fuel)),
    };
    const saved = station
      ? await opsApi.stations.update(station.id, body)
      : await opsApi.stations.create({ operatorId: operatorId ?? operator, station: body });
    toast(t(editing ? 'stationForm.saved' : 'stationForm.created'));
    onSaved(saved);
  };

  return (
    <FormSheet
      open={open}
      onOpenChange={onOpenChange}
      title={t(editing ? 'stationForm.editTitle' : 'stationForm.newTitle')}
      submitLabel={t(editing ? 'common.edit' : 'stationForm.create')}
      onSubmit={submit}
      onError={(error) => {
        const name = error.field ? API_FIELD[error.field] : undefined;
        if (name) setErrors((previous) => ({ ...previous, [name]: error.message }));
      }}
    >
      {chooseOperator && (
        <SelectField
          label={t('stationForm.operator')}
          value={operator}
          error={errors.operator}
          onValueChange={(value) => {
            setOperator(value);
            setErrors((previous) => ({ ...previous, operator: undefined }));
          }}
          options={[
            { value: '', label: operators.loading ? t('stationForm.operatorLoading') : t('stationForm.operatorChoose') },
            ...(operators.data?.items ?? []).map((item) => ({ value: item.id, label: locale === 'ar' ? item.nameAr : item.nameEn })),
          ]}
        />
      )}
      <TextField label={t('stationForm.nameAr')} maxLength={200} lang="ar" dir="rtl" {...field('nameAr')} />
      <TextField label={t('stationForm.nameEn')} maxLength={200} lang="en" dir="ltr" {...field('nameEn')} />
      <TextField label={t('common.optional', { label: t('stationForm.address') })} maxLength={300} {...field('address')} />
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField
          label={t('common.optional', { label: t('stationForm.latitude') })}
          inputMode="decimal"
          dir="ltr"
          autoComplete="off"
          {...field('latitude')}
        />
        <TextField
          label={t('common.optional', { label: t('stationForm.longitude') })}
          inputMode="decimal"
          dir="ltr"
          autoComplete="off"
          {...field('longitude')}
        />
      </div>
      <TextField
        label={t('common.optional', { label: t('stationForm.timeZone') })}
        hint={t('stationForm.timeZoneHint')}
        maxLength={60}
        dir="ltr"
        autoComplete="off"
        spellCheck={false}
        {...field('timeZone')}
      />
      <div className="flex flex-col gap-1.5">
        <span className="text-sm font-medium">{t('stationForm.connectivity')}</span>
        <SegmentedControl<Connectivity>
          label={t('stationForm.connectivity')}
          value={connectivity}
          onValueChange={setConnectivity}
          options={[
            { value: 'NORMAL', label: t('enums.connectivity.NORMAL') },
            { value: 'LOW', label: t('enums.connectivity.LOW') },
          ]}
        />
        <p className="text-xs text-muted">{t('stationForm.connectivityHint')}</p>
      </div>
      <fieldset className="flex flex-col gap-2">
        <legend className="mb-1.5 text-sm font-medium">{t('stationForm.fuelTypes')}</legend>
        <div className="grid gap-2 sm:grid-cols-2">
          {FUEL_TYPES.map((fuel) => (
            <label key={fuel} className="flex min-h-11 cursor-pointer items-center gap-3 rounded-control border border-input-border bg-input px-3 text-sm">
              <input type="checkbox" className="size-4 accent-primary" checked={fuelTypes.includes(fuel)} onChange={() => toggleFuel(fuel)} />
              {t(`enums.fuel.${fuel}`)}
            </label>
          ))}
        </div>
        {errors.fuelTypes && (
          <p role="alert" className="text-xs text-danger">
            {errors.fuelTypes}
          </p>
        )}
      </fieldset>
    </FormSheet>
  );
}
