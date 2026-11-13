import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Banner, SelectField, TextField } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { USER_ROLES, adminApi, type Company, type CreatedUser, type Operator, type UserRole } from '../api';
import { OneTimeSecret } from '../components';
import { SecretFlowSheet } from './secret-sheet';

type ScopeKind = 'PLATFORM' | 'COMPANY' | 'OPERATOR';

/** Which kind of organisation a role belongs to. */
const scopeOf = (role: UserRole): ScopeKind =>
  role === 'FLEET_MANAGER' || role === 'ACCOUNTANT' ? 'COMPANY' : role === 'STATION_MANAGER' ? 'OPERATOR' : 'PLATFORM';

type Fields = { fullName?: string; email?: string; scope?: string };

/** The e-mail with the temporary password, shown once after the account is created. */
function CreatedResult({ result }: { result: CreatedUser }) {
  const { t } = useTranslation('live');
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-1">
        <span className="text-sm font-medium">{t('userForm.email')}</span>
        <span dir="ltr" className="font-mono text-lg font-semibold">
          {result.user.email}
        </span>
      </div>
      <OneTimeSecret label={t('userForm.temporaryPassword')} value={result.temporaryPassword} group={4} />
      <Banner tone="info" title={t('userForm.mustChange')} description={t('userForm.mustChangeHint')} />
    </div>
  );
}

/** Super Admin: create a user. The temporary password is shown once, in the same sheet. */
export function UserFormSheet({
  companies,
  operators,
  onClose,
  onChanged,
}: {
  companies: Company[];
  operators: Operator[];
  onClose: () => void;
  onChanged: () => void;
}) {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const [fullName, setFullName] = useState('');
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<UserRole>('FLEET_MANAGER');
  const [scopeId, setScopeId] = useState('');
  const [errors, setErrors] = useState<Fields>({});
  const kind = scopeOf(role);
  const organisations =
    kind === 'COMPANY'
      ? companies.map((company) => ({ value: company.id, label: locale === 'ar' ? company.nameAr : company.nameEn }))
      : operators.map((operator) => ({ value: operator.id, label: locale === 'ar' ? operator.nameAr : operator.nameEn }));

  return (
    <SecretFlowSheet<CreatedUser>
      title={t('userForm.title')}
      description={t('userForm.hint')}
      submitLabel={t('userForm.create')}
      resultTitle={t('userForm.created')}
      doneLabel={t('userForm.done')}
      form={
        <>
          <TextField
            label={t('userForm.fullName')}
            maxLength={200}
            autoComplete="off"
            value={fullName}
            error={errors.fullName}
            onChange={(event) => {
              setFullName(event.target.value);
              setErrors((previous) => ({ ...previous, fullName: undefined }));
            }}
          />
          <TextField
            label={t('userForm.email')}
            type="email"
            dir="ltr"
            maxLength={254}
            autoComplete="off"
            value={email}
            error={errors.email}
            onChange={(event) => {
              setEmail(event.target.value);
              setErrors((previous) => ({ ...previous, email: undefined }));
            }}
          />
          <SelectField<UserRole>
            label={t('userForm.role')}
            value={role}
            onValueChange={(next) => {
              setRole(next);
              setScopeId('');
              setErrors((previous) => ({ ...previous, scope: undefined }));
            }}
            options={USER_ROLES.map((value) => ({ value, label: t(`enums.userRole.${value}`) }))}
          />
          {kind !== 'PLATFORM' && (
            <SelectField
              label={t(kind === 'COMPANY' ? 'userForm.company' : 'userForm.operator')}
              value={scopeId}
              error={errors.scope}
              onValueChange={(next) => {
                setScopeId(next);
                setErrors((previous) => ({ ...previous, scope: undefined }));
              }}
              options={[{ value: '', label: t('userForm.choose') }, ...organisations]}
            />
          )}
        </>
      }
      run={async () => {
        const found: Fields = {};
        if (!fullName.trim()) found.fullName = t('errors.required');
        if (!email.trim()) found.email = t('errors.required');
        else if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.trim())) found.email = t('userForm.emailInvalid');
        if (kind !== 'PLATFORM' && !scopeId) found.scope = t('errors.required');
        if (Object.keys(found).length > 0) {
          setErrors(found);
          return false;
        }
        return adminApi.users.create({
          fullName: fullName.trim(),
          email: email.trim(),
          role,
          scope: kind === 'PLATFORM' ? { type: 'PLATFORM' } : { type: kind, id: scopeId },
        });
      }}
      onError={(error) => {
        if (error.field === 'email') setErrors((previous) => ({ ...previous, email: error.message }));
      }}
      renderResult={(result) => <CreatedResult result={result} />}
      onChanged={onChanged}
      onClose={onClose}
    />
  );
}
