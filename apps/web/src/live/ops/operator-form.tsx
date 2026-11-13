import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { TextField, toast } from '@/design-system';
import { opsApi, type Operator, type OperatorBody } from '../api';
import { FormSheet } from '../components';
import { optional } from '../input';

type Fields = Record<
  'legalName' | 'nameAr' | 'nameEn' | 'taxId' | 'commercialRegistration' | 'contactName' | 'contactPhone' | 'contactEmail',
  string
>;

const empty: Fields = {
  legalName: '',
  nameAr: '',
  nameEn: '',
  taxId: '',
  commercialRegistration: '',
  contactName: '',
  contactPhone: '',
  contactEmail: '',
};

function fromOperator(operator: Operator): Fields {
  return {
    legalName: operator.legalName,
    nameAr: operator.nameAr,
    nameEn: operator.nameEn,
    taxId: operator.taxId ?? '',
    commercialRegistration: operator.commercialRegistration ?? '',
    contactName: operator.contactName ?? '',
    contactPhone: operator.contactPhone ?? '',
    contactEmail: operator.contactEmail ?? '',
  };
}

/** Maps the API's field names to this form's, so a refusal marks the right input. */
const API_FIELD: Record<string, keyof Fields> = {
  legalName: 'legalName',
  'operator.legalName': 'legalName',
  taxId: 'taxId',
  'operator.taxId': 'taxId',
  contactEmail: 'contactEmail',
  'operator.contactEmail': 'contactEmail',
};

/** Registering a station operator (no `operator`) or editing one. */
export function OperatorFormSheet({
  open,
  onOpenChange,
  operator,
  onSaved,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  operator?: Operator;
  onSaved: (operator: Operator) => void;
}) {
  const { t } = useTranslation('live');
  const [fields, setFields] = useState<Fields>(() => (operator ? fromOperator(operator) : empty));
  const [errors, setErrors] = useState<Partial<Record<keyof Fields, string>>>({});
  const editing = Boolean(operator);

  const field = (name: keyof Fields) => ({
    value: fields[name],
    error: errors[name],
    onChange: (event: { target: { value: string } }) => {
      setFields((previous) => ({ ...previous, [name]: event.target.value }));
      setErrors((previous) => ({ ...previous, [name]: undefined }));
    },
  });

  const submit = async () => {
    const found: Partial<Record<keyof Fields, string>> = {};
    for (const name of ['legalName', 'nameAr', 'nameEn'] as const) {
      if (!fields[name].trim()) found[name] = t('errors.required');
    }
    if (Object.keys(found).length > 0) {
      setErrors(found);
      return false;
    }
    const body: OperatorBody = {
      legalName: fields.legalName.trim(),
      nameAr: fields.nameAr.trim(),
      nameEn: fields.nameEn.trim(),
      taxId: optional(fields.taxId),
      commercialRegistration: optional(fields.commercialRegistration),
      contactName: optional(fields.contactName),
      contactPhone: optional(fields.contactPhone),
      contactEmail: optional(fields.contactEmail),
    };
    const saved = operator ? await opsApi.operators.update(operator.id, body) : await opsApi.operators.create(body);
    toast(t(editing ? 'operatorForm.saved' : 'operatorForm.created'));
    if (!editing) setFields(empty);
    onSaved(saved);
  };

  return (
    <FormSheet
      open={open}
      onOpenChange={onOpenChange}
      title={t(editing ? 'operatorForm.editTitle' : 'operatorForm.newTitle')}
      submitLabel={t(editing ? 'common.edit' : 'operatorForm.create')}
      onSubmit={submit}
      onError={(error) => {
        const name = error.field ? API_FIELD[error.field] : undefined;
        if (name) setErrors((previous) => ({ ...previous, [name]: error.message }));
      }}
    >
      <TextField label={t('operatorForm.legalName')} maxLength={200} autoComplete="organization" {...field('legalName')} />
      <TextField label={t('operatorForm.nameAr')} maxLength={200} lang="ar" dir="rtl" {...field('nameAr')} />
      <TextField label={t('operatorForm.nameEn')} maxLength={200} lang="en" dir="ltr" {...field('nameEn')} />
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField label={t('common.optional', { label: t('operatorForm.taxId') })} maxLength={50} dir="ltr" {...field('taxId')} />
        <TextField
          label={t('common.optional', { label: t('operatorForm.commercialRegistration') })}
          maxLength={50}
          dir="ltr"
          {...field('commercialRegistration')}
        />
      </div>
      <TextField label={t('common.optional', { label: t('operatorForm.contactName') })} maxLength={200} {...field('contactName')} />
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField
          label={t('common.optional', { label: t('operatorForm.contactPhone') })}
          maxLength={30}
          type="tel"
          dir="ltr"
          {...field('contactPhone')}
        />
        <TextField
          label={t('common.optional', { label: t('operatorForm.contactEmail') })}
          maxLength={254}
          type="email"
          dir="ltr"
          {...field('contactEmail')}
        />
      </div>
    </FormSheet>
  );
}
