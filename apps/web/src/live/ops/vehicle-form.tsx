import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { SegmentedControl, SelectField, TextField, toast } from '@/design-system';
import { FUEL_TYPES, opsApi, type Department, type FuelType, type Vehicle, type VehicleBody, type VehicleTier } from '../api';
import { FormSheet } from '../components';
import { mlToInput, parseLitres, parseWhole } from '../input';

type Text = 'plate' | 'make' | 'model' | 'year' | 'tank' | 'vipCeiling';
const NO_DEPARTMENT = '';

const API_FIELD: Record<string, Text | 'department'> = {
  plate: 'plate',
  'vehicle.plate': 'plate',
  tankCapacityMl: 'tank',
  'vehicle.tankCapacityMl': 'tank',
  vipMonthlyCeilingMl: 'vipCeiling',
  'vehicle.vipMonthlyCeilingMl': 'vipCeiling',
  year: 'year',
  'vehicle.year': 'year',
  departmentId: 'department',
  'vehicle.departmentId': 'department',
};

/** Adding a vehicle to a company (no `vehicle`), or editing one. The company never changes. */
export function VehicleFormSheet({
  open,
  onOpenChange,
  companyId,
  departments,
  vehicle,
  onSaved,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  companyId: string;
  departments: Department[];
  vehicle?: Vehicle;
  onSaved: (vehicle: Vehicle) => void;
}) {
  const { t } = useTranslation('live');
  const editing = Boolean(vehicle);
  const [text, setText] = useState<Record<Text, string>>(() => ({
    plate: vehicle?.plate ?? '',
    make: vehicle?.make ?? '',
    model: vehicle?.model ?? '',
    year: vehicle?.year ? String(vehicle.year) : '',
    tank: mlToInput(vehicle?.tankCapacityMl),
    vipCeiling: mlToInput(vehicle?.vipMonthlyCeilingMl),
  }));
  const [fuel, setFuel] = useState<FuelType>(vehicle?.fuelType ?? 'GAS_92');
  const [tier, setTier] = useState<VehicleTier>(vehicle?.tier ?? 'NORMAL');
  const [department, setDepartment] = useState<string>(vehicle?.departmentId ?? NO_DEPARTMENT);
  const [errors, setErrors] = useState<Partial<Record<Text | 'department', string>>>({});

  const field = (name: Text) => ({
    value: text[name],
    error: errors[name],
    onChange: (event: { target: { value: string } }) => {
      setText((previous) => ({ ...previous, [name]: event.target.value }));
      setErrors((previous) => ({ ...previous, [name]: undefined }));
    },
  });

  const submit = async () => {
    const found: Partial<Record<Text, string>> = {};
    for (const name of ['plate', 'make', 'model', 'tank'] as const) {
      if (!text[name].trim()) found[name] = t('errors.required');
    }
    const tank = parseLitres(text.tank);
    if (text.tank.trim() && (tank === null || tank <= 0)) found.tank = t('errors.invalidVolume');
    const year = text.year.trim() ? parseWhole(text.year) : undefined;
    if (year === null || (year !== undefined && (year < 1950 || year > 2100))) found.year = t('errors.outOfRange', { min: 1950, max: 2100 });
    const ceiling = tier === 'VIP' && text.vipCeiling.trim() ? parseLitres(text.vipCeiling) : undefined;
    if (ceiling === null || ceiling === 0) found.vipCeiling = t('errors.invalidVolume');
    if (Object.keys(found).length > 0) {
      setErrors(found);
      return false;
    }
    const body: VehicleBody = {
      departmentId: department === NO_DEPARTMENT ? undefined : department,
      plate: text.plate.trim(),
      make: text.make.trim(),
      model: text.model.trim(),
      year: year ?? undefined,
      fuelType: fuel,
      tankCapacityMl: tank ?? 0,
      tier,
      vipMonthlyCeilingMl: ceiling ?? undefined,
    };
    const saved = vehicle ? await opsApi.vehicles.update(vehicle.id, body) : await opsApi.vehicles.create({ companyId, vehicle: body });
    toast(t(editing ? 'vehicleForm.saved' : 'vehicleForm.created'));
    onSaved(saved);
  };

  return (
    <FormSheet
      open={open}
      onOpenChange={onOpenChange}
      title={t(editing ? 'vehicleForm.editTitle' : 'vehicleForm.newTitle')}
      submitLabel={t(editing ? 'common.edit' : 'vehicleForm.create')}
      onSubmit={submit}
      onError={(error) => {
        const name = error.field ? API_FIELD[error.field] : undefined;
        if (name) setErrors((previous) => ({ ...previous, [name]: error.message }));
      }}
    >
      <TextField label={t('vehicleForm.plate')} hint={t('vehicleForm.plateHint')} maxLength={20} {...field('plate')} />
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField label={t('vehicleForm.make')} maxLength={60} {...field('make')} />
        <TextField label={t('vehicleForm.model')} maxLength={60} {...field('model')} />
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField label={t('common.optional', { label: t('vehicleForm.year') })} inputMode="numeric" dir="ltr" {...field('year')} />
        <TextField label={t('vehicleForm.tank')} inputMode="decimal" dir="ltr" {...field('tank')} />
      </div>
      <SelectField<FuelType>
        label={t('vehicleForm.fuel')}
        value={fuel}
        onValueChange={setFuel}
        options={FUEL_TYPES.map((value) => ({ value, label: t(`enums.fuel.${value}`) }))}
      />
      <SelectField
        label={t('vehicleForm.department')}
        value={department}
        error={errors.department}
        onValueChange={(value) => {
          setDepartment(value);
          setErrors((previous) => ({ ...previous, department: undefined }));
        }}
        options={[
          { value: NO_DEPARTMENT, label: t('vehicleForm.noDepartment') },
          ...departments.filter((item) => item.active).map((item) => ({ value: item.id, label: item.name })),
        ]}
      />
      <div className="flex flex-col gap-1.5">
        <span className="text-sm font-medium">{t('vehicleForm.tier')}</span>
        <SegmentedControl<VehicleTier>
          label={t('vehicleForm.tier')}
          value={tier}
          onValueChange={setTier}
          options={[
            { value: 'NORMAL', label: t('enums.tier.NORMAL') },
            { value: 'VIP', label: t('enums.tier.VIP') },
          ]}
        />
      </div>
      {tier === 'VIP' && (
        <TextField
          label={t('common.optional', { label: t('vehicleForm.vipCeiling') })}
          hint={t('vehicleForm.vipCeilingHint')}
          inputMode="decimal"
          dir="ltr"
          {...field('vipCeiling')}
        />
      )}
    </FormSheet>
  );
}
