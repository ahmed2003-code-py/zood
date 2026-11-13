import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { TextField, toast } from '@/design-system';
import { opsApi, type Company, type CompanyBody } from '../api';
import { FormSheet } from '../components';
import { optional, parsePounds, parseWhole, piastresToInput } from '../input';

type Fields = Record<
  'legalName' | 'nameAr' | 'nameEn' | 'taxId' | 'commercialRegistration' | 'contactName' | 'contactPhone' | 'contactEmail' | 'lowBalance' | 'cooldown',
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
  lowBalance: '',
  cooldown: '',
};

function fromCompany(company: Company): Fields {
  return {
    legalName: company.legalName,
    nameAr: company.nameAr,
    nameEn: company.nameEn,
    taxId: company.taxId ?? '',
    commercialRegistration: company.commercialRegistration ?? '',
    contactName: company.contactName ?? '',
    contactPhone: company.contactPhone ?? '',
    contactEmail: company.contactEmail ?? '',
    lowBalance: piastresToInput(company.lowBalanceThresholdPiastres),
    cooldown: String(company.cooldownMinutes),
  };
}

/** Maps the API's field names to this form's, so a refusal marks the right input. */
const API_FIELD: Record<string, keyof Fields> = {
  'company.legalName': 'legalName',
  legalName: 'legalName',
  taxId: 'taxId',
  'company.taxId': 'taxId',
  contactEmail: 'contactEmail',
  'company.contactEmail': 'contactEmail',
  lowBalanceThresholdPiastres: 'lowBalance',
  cooldownMinutes: 'cooldown',
};

/**
 * Onboarding a company (no `company`) or editing one. On edit only identity and contacts change:
 * the cooldown and the low-balance alert are financial controls with their own, Super Admin only,
 * action on the company page.
 */
export function CompanyFormSheet({
  open,
  onOpenChange,
  company,
  onSaved,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  company?: Company;
  onSaved: (company: Company) => void;
}) {
  const { t } = useTranslation('live');
  const [fields, setFields] = useState<Fields>(() => (company ? fromCompany(company) : empty));
  const [errors, setErrors] = useState<Partial<Record<keyof Fields, string>>>({});
  const editing = Boolean(company);

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
    const lowBalance = editing ? 0 : parsePounds(fields.lowBalance || '0');
    if (lowBalance === null) found.lowBalance = t('errors.invalidAmount');
    const cooldown = fields.cooldown.trim() ? parseWhole(fields.cooldown) : undefined;
    if (!editing && fields.cooldown.trim() && (cooldown === null || cooldown === undefined || cooldown < 120 || cooldown > 240)) {
      found.cooldown = t('errors.outOfRange', { min: 120, max: 240 });
    }
    if (Object.keys(found).length > 0) {
      setErrors(found);
      return false;
    }
    const body: CompanyBody = {
      legalName: fields.legalName.trim(),
      nameAr: fields.nameAr.trim(),
      nameEn: fields.nameEn.trim(),
      taxId: optional(fields.taxId),
      commercialRegistration: optional(fields.commercialRegistration),
      contactName: optional(fields.contactName),
      contactPhone: optional(fields.contactPhone),
      contactEmail: optional(fields.contactEmail),
    };
    const saved = company
      ? await opsApi.companies.update(company.id, body)
      : await opsApi.companies.create({
          company: body,
          lowBalanceThresholdPiastres: lowBalance ?? 0,
          cooldownMinutes: cooldown ?? undefined,
        });
    toast(t(editing ? 'companyForm.saved' : 'companyForm.created'));
    if (!editing) setFields(empty);
    onSaved(saved);
  };

  return (
    <FormSheet
      open={open}
      onOpenChange={onOpenChange}
      title={t(editing ? 'companyForm.editTitle' : 'companyForm.newTitle')}
      submitLabel={t(editing ? 'common.edit' : 'companyForm.create')}
      onSubmit={submit}
      onError={(error) => {
        const name = error.field ? API_FIELD[error.field] : undefined;
        if (name) setErrors((previous) => ({ ...previous, [name]: error.message }));
      }}
    >
      <TextField label={t('companyForm.legalName')} maxLength={200} autoComplete="organization" {...field('legalName')} />
      <TextField label={t('companyForm.nameAr')} maxLength={200} lang="ar" dir="rtl" {...field('nameAr')} />
      <TextField label={t('companyForm.nameEn')} maxLength={200} lang="en" dir="ltr" {...field('nameEn')} />
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField label={t('common.optional', { label: t('companyForm.taxId') })} maxLength={50} dir="ltr" {...field('taxId')} />
        <TextField
          label={t('common.optional', { label: t('companyForm.commercialRegistration') })}
          maxLength={50}
          dir="ltr"
          {...field('commercialRegistration')}
        />
      </div>
      <TextField label={t('common.optional', { label: t('companyForm.contactName') })} maxLength={200} {...field('contactName')} />
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField
          label={t('common.optional', { label: t('companyForm.contactPhone') })}
          maxLength={30}
          type="tel"
          dir="ltr"
          {...field('contactPhone')}
        />
        <TextField
          label={t('common.optional', { label: t('companyForm.contactEmail') })}
          maxLength={254}
          type="email"
          dir="ltr"
          {...field('contactEmail')}
        />
      </div>
      {!editing && (
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            label={t('companyForm.lowBalance')}
            hint={t('companyForm.lowBalanceHint')}
            inputMode="decimal"
            dir="ltr"
            {...field('lowBalance')}
          />
          <TextField
            label={t('common.optional', { label: t('companyForm.cooldown') })}
            hint={t('companyForm.cooldownHint')}
            inputMode="numeric"
            dir="ltr"
            {...field('cooldown')}
          />
        </div>
      )}
    </FormSheet>
  );
}
