import { CheckCircle2, Circle, FileUp, Landmark, Pencil, Plus, Scale, ShieldCheck, Snowflake, Sun } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';
import { useSession } from '@/auth/session-store';
import {
  Banner,
  Button,
  DataView,
  DateTimeText,
  ErrorState,
  Icon,
  ListRow,
  Money,
  Plate,
  Skeleton,
  Tabs,
  StatusChip,
  TextField,
  toast,
  type DataColumn,
} from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatVolume } from '@/lib/format';
import { BackLink, DetailList, ListPanel, PageHeader, PageSection } from '@/shell/page';
import { opsApi, type Company, type Department, type Onboarding, type Vehicle } from '../api';
import { CompanyStatusChip, VehicleStatusChip } from '../chips';
import { EmptyList, FormSheet, PagedList, ReasonSheet } from '../components';
import { optional, parsePounds, parseWhole, piastresToInput } from '../input';
import { usePagedResource, useResource, type Resource } from '../use-resource';
import { BalanceCard, StatementList, TransferSheet } from '../wallet';
import { CompanyFormSheet } from './company-form';
import { VehicleFormSheet } from './vehicle-form';

type Dialog = 'edit' | 'status' | 'policy' | 'department' | 'vehicle' | 'credit' | 'adjustment' | undefined;

const CHECK_KEYS = ['ACTIVE', 'CONTACT', 'REGISTRATION', 'DEPARTMENTS', 'VEHICLES', 'TAGS', 'LIMITS', 'WALLET_FUNDED', 'COMPANY_USERS'] as const;
type CheckKey = (typeof CHECK_KEYS)[number];
const isCheckKey = (key: string): key is CheckKey => (CHECK_KEYS as readonly string[]).includes(key);

/** What is still missing before the company can fuel: required checks block, recommended ones do not. */
function OnboardingPanel({ resource }: { resource: Resource<Onboarding> }) {
  const { t } = useTranslation('live');
  if (resource.error && !resource.data) return <ErrorState onRetry={resource.reload} className="py-6" />;
  const data = resource.data;
  if (!data) return <Skeleton className="h-48" aria-busy />;
  return (
    <ListPanel>
      <div className="flex items-center justify-between gap-3 border-b border-border px-4 py-3">
        <p className="text-sm text-muted">{t('onboarding.hint')}</p>
        <StatusChip tone={data.ready ? 'success' : 'warning'} label={t(data.ready ? 'onboarding.ready' : 'onboarding.notReady')} />
      </div>
      <ul className="divide-y divide-border">
        {data.checks.map((check) => (
          <li key={check.key} className="flex items-center gap-3 px-4 py-2.5">
            <span className={check.done ? 'text-success' : 'text-muted'}>
              <Icon icon={check.done ? CheckCircle2 : Circle} size={18} />
            </span>
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium">{isCheckKey(check.key) ? t(`onboarding.checks.${check.key}`) : check.key}</p>
              <p className="text-xs text-muted">{t(check.done ? 'onboarding.done' : 'onboarding.todo')}</p>
            </div>
            {check.count !== undefined && check.count !== null && <span className="text-sm tabular-nums text-muted">{check.count}</span>}
            <StatusChip tone={check.required ? 'brand' : 'neutral'} label={t(check.required ? 'onboarding.required' : 'onboarding.recommended')} />
          </li>
        ))}
      </ul>
    </ListPanel>
  );
}

/** Ops: one company, its financial controls, departments and vehicles. */
export function CompanyPage() {
  const { id = '' } = useParams();
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const navigate = useNavigate();
  const role = useSession((state) => state.user?.role);
  const [dialog, setDialog] = useState<Dialog>();
  const company = useResource((signal) => opsApi.companies.get(id, signal), [id]);
  const departments = usePagedResource((page, signal) => opsApi.companies.departments(id, page, signal), [id], 100);
  const vehicles = usePagedResource((page, signal) => opsApi.vehicles.list({ ...page, companyId: id }, signal), [id]);
  const wallet = useResource((signal) => opsApi.wallets.get(id, signal), [id]);
  const statement = usePagedResource((page, signal) => opsApi.wallets.statement(id, page, signal), [id], 10);
  const onboarding = useResource((signal) => opsApi.companies.onboarding(id, signal), [id]);
  const accounts = useResource((signal) => opsApi.topUps.receivingAccounts(signal), []);
  const close = () => setDialog(undefined);

  if (company.error && !company.data) {
    return (
      <>
        <BackLink to="/ops/companies" label={t('company.back')} />
        <ErrorState onRetry={company.reload} />
      </>
    );
  }
  if (!company.data) {
    return (
      <div className="flex flex-col gap-4" aria-busy>
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  const data = company.data;
  const name = locale === 'ar' ? data.nameAr : data.nameEn;
  const frozen = data.status === 'FROZEN';
  const vehicleColumns: DataColumn<Vehicle>[] = [
    { id: 'plate', header: t('vehicleForm.plate'), cell: (vehicle) => <Plate value={vehicle.plate} size="sm" /> },
    { id: 'fleetCode', header: t('vehicleForm.fleetCode'), cell: (vehicle) => <span dir="ltr">{vehicle.fleetCode ?? t('common.notSet')}</span> },
    { id: 'model', header: t('vehicleForm.model'), cell: (vehicle) => `${vehicle.make} ${vehicle.model}` },
    { id: 'fuel', header: t('vehicleForm.fuel'), cell: (vehicle) => t(`enums.fuel.${vehicle.fuelType}`) },
    { id: 'tank', header: t('vehicle.tank'), align: 'end', cell: (vehicle) => formatVolume(vehicle.tankCapacityMl, locale, 0) },
    { id: 'tier', header: t('vehicleForm.tier'), cell: (vehicle) => t(`enums.tier.${vehicle.tier}`) },
    { id: 'status', header: t('vehicle.status'), cell: (vehicle) => <VehicleStatusChip status={vehicle.status} /> },
  ];

  return (
    <>
      <BackLink to="/ops/companies" label={t('company.back')} />
      <PageHeader
        title={name}
        description={<bdi>{data.legalName}</bdi>}
        actions={
          <>
            <Button variant="secondary" leadingIcon={Pencil} onClick={() => setDialog('edit')}>
              {t('company.edit')}
            </Button>
            <Button variant={frozen ? 'primary' : 'secondary'} leadingIcon={frozen ? Sun : Snowflake} onClick={() => setDialog('status')}>
              {t(frozen ? 'company.unfreeze' : 'company.freeze')}
            </Button>
          </>
        }
      />
      {frozen && <Banner tone="warning" className="mb-6" title={t('company.frozenBanner')} description={t('company.frozenBannerHint')} />}

      <Tabs
        label={name}
        items={[
          {
            value: 'overview',
            label: t('company.overview'),
            content: (
              <div className="grid grid-cols-1 gap-8 lg:grid-cols-2">
                <div className="flex min-w-0 flex-col gap-8">
                  <PageSection title={t('onboarding.title')}>
                    <OnboardingPanel resource={onboarding} />
                  </PageSection>
                  <PageSection
                    title={t('company.departments')}
                    action={
                      <Button variant="ghost" size="sm" leadingIcon={Plus} onClick={() => setDialog('department')}>
                        {t('company.addDepartment')}
                      </Button>
                    }
                  >
                    <ListPanel>
                      <PagedList
                        resource={departments}
                        empty={<EmptyList title={t('company.noDepartments')} description={t('company.noDepartmentsHint')} />}
                      >
                        {(items) => (
                          <div className="divide-y divide-border">
                            {items.map((department) => (
                              <ListRow
                                key={department.id}
                                title={department.name}
                                trailing={
                                  department.monthlyBudgetPiastres != null ? (
                                    <Money value={department.monthlyBudgetPiastres} precision="whole" />
                                  ) : (
                                    <span className="text-sm font-normal text-muted">{t('company.noBudget')}</span>
                                  )
                                }
                              />
                            ))}
                          </div>
                        )}
                      </PagedList>
                    </ListPanel>
                  </PageSection>
                </div>
                <div className="flex min-w-0 flex-col gap-8">
                  <PageSection title={t('company.details')}>
                    <ListPanel>
                      <DetailList
                        rows={[
                          { label: t('companies.columns.status'), value: <CompanyStatusChip status={data.status} /> },
                          { label: t('companyForm.taxId'), value: <span dir="ltr">{data.taxId ?? t('common.notSet')}</span> },
                          {
                            label: t('companyForm.commercialRegistration'),
                            value: <span dir="ltr">{data.commercialRegistration ?? t('common.notSet')}</span>,
                          },
                          { label: t('companyForm.contactName'), value: data.contactName ?? t('common.notSet') },
                          { label: t('companyForm.contactPhone'), value: <span dir="ltr">{data.contactPhone ?? t('common.notSet')}</span> },
                          { label: t('companyForm.contactEmail'), value: <span dir="ltr">{data.contactEmail ?? t('common.notSet')}</span> },
                          { label: t('common.createdAt'), value: <DateTimeText value={data.createdAt} variant="date" /> },
                        ]}
                      />
                    </ListPanel>
                  </PageSection>
                  <PageSection
                    title={t('company.policy')}
                    action={
                      role === 'SUPER_ADMIN' && (
                        <Button variant="ghost" size="sm" leadingIcon={ShieldCheck} onClick={() => setDialog('policy')}>
                          {t('common.edit')}
                        </Button>
                      )
                    }
                  >
                    <ListPanel>
                      <DetailList
                        rows={[
                          { label: t('company.cooldown'), value: t('common.minutes', { count: data.cooldownMinutes }) },
                          { label: t('company.lowBalance'), value: <Money value={data.lowBalanceThresholdPiastres} precision="whole" /> },
                        ]}
                      />
                    </ListPanel>
                    {role !== 'SUPER_ADMIN' && <p className="text-xs text-muted">{t('company.policyHint')}</p>}
                  </PageSection>
                </div>
              </div>
            ),
          },
          {
            value: 'vehicles',
            label: t('company.vehicles'),
            content: (
              <PageSection
                description={t('company.vehiclesHint')}
                action={
                  <div className="flex flex-wrap gap-1">
                    <Button variant="ghost" size="sm" leadingIcon={FileUp} onClick={() => navigate(`/ops/companies/${id}/import`)}>
                      {t('company.importVehicles')}
                    </Button>
                    <Button variant="ghost" size="sm" leadingIcon={Plus} onClick={() => setDialog('vehicle')}>
                      {t('company.addVehicle')}
                    </Button>
                  </div>
                }
              >
                <ListPanel>
                  <PagedList
                    resource={vehicles}
                    empty={
                      <EmptyList
                        title={t('company.noVehicles')}
                        description={t('company.noVehiclesHint')}
                        action={
                          <Button leadingIcon={Plus} onClick={() => setDialog('vehicle')}>
                            {t('company.addVehicle')}
                          </Button>
                        }
                      />
                    }
                  >
                    {(items) => (
                      <DataView
                        caption={t('company.vehicles')}
                        rows={items}
                        columns={vehicleColumns}
                        getRowId={(vehicle) => vehicle.id}
                        onRowClick={(vehicle) => navigate(`/ops/vehicles/${vehicle.id}`)}
                        renderListItem={(vehicle) => (
                          <ListRow
                            title={<Plate value={vehicle.plate} size="sm" />}
                            subtitle={[vehicle.fleetCode, `${vehicle.make} ${vehicle.model}`, t(`enums.fuel.${vehicle.fuelType}`)].filter(Boolean).join(' · ')}
                            trailing={<VehicleStatusChip status={vehicle.status} />}
                            onClick={() => navigate(`/ops/vehicles/${vehicle.id}`)}
                          />
                        )}
                      />
                    )}
                  </PagedList>
                </ListPanel>
              </PageSection>
            ),
          },
          {
            value: 'wallet',
            label: t('wallet.title'),
            content: (
              <div className="max-w-3xl">
                <PageSection>
                  <BalanceCard
                    wallet={wallet.data}
                    loading={wallet.loading}
                    action={
                      <div className="flex flex-wrap gap-2">
                        <Button variant="secondary" leadingIcon={Landmark} onClick={() => setDialog('credit')}>
                          {t('wallet.credit')}
                        </Button>
                        <Button variant="secondary" leadingIcon={Scale} onClick={() => setDialog('adjustment')}>
                          {t('adjustments.request')}
                        </Button>
                      </div>
                    }
                  />
                  <h3 className="mt-3 text-base font-semibold">{t('wallet.statement')}</h3>
                  <ListPanel>
                    <StatementList resource={statement} />
                  </ListPanel>
                </PageSection>
              </div>
            ),
          },
        ]}
      />

      {dialog === 'credit' && (
        <TransferSheet
          title={t('wallet.creditTitle')}
          description={t('wallet.creditHint')}
          submitLabel={t('wallet.credit')}
          doneMessage={t('wallet.credited')}
          send={(body) => opsApi.wallets.credit(id, body)}
          accounts={accounts.data && accounts.data.length > 0 ? accounts.data : ['MAIN']}
          onClose={close}
          onDone={() => {
            wallet.reload();
            statement.reload();
            onboarding.reload();
          }}
        />
      )}
      {dialog === 'adjustment' && <AdjustmentSheet companyId={id} onClose={close} />}
      {dialog === 'edit' && (
        <CompanyFormSheet open onOpenChange={close} company={data} onSaved={company.reload} />
      )}
      <ReasonSheet
        open={dialog === 'status'}
        onOpenChange={close}
        title={t(frozen ? 'company.unfreezeTitle' : 'company.freezeTitle', { name })}
        description={t(frozen ? 'company.unfreezeHint' : 'company.freezeHint')}
        submitLabel={t(frozen ? 'company.unfreeze' : 'company.freeze')}
        submitVariant={frozen ? 'primary' : 'danger'}
        onConfirm={async (reason) => {
          await opsApi.companies.setStatus(id, { status: frozen ? 'ACTIVE' : 'FROZEN', reason });
          toast(t(frozen ? 'company.unfrozen' : 'company.frozen'));
          company.reload();
        }}
      />
      {dialog === 'policy' && <PolicySheet company={data} onClose={close} onSaved={company.reload} />}
      {dialog === 'department' && <DepartmentSheet companyId={id} onClose={close} onSaved={departments.reload} />}
      {dialog === 'vehicle' && (
        <VehicleFormSheet
          open
          onOpenChange={close}
          companyId={id}
          departments={departments.items}
          onSaved={(vehicle) => navigate(`/ops/vehicles/${vehicle.id}`)}
        />
      )}
    </>
  );
}

/**
 * A credit with no bank transfer behind it. It waits for a manager and never credits at once. The
 * idempotency key is made when the sheet opens, so a double submit is one request.
 */
function AdjustmentSheet({ companyId, onClose }: { companyId: string; onClose: () => void }) {
  const { t } = useTranslation('live');
  const [idempotencyKey] = useState(() => crypto.randomUUID());
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');
  const [errors, setErrors] = useState<{ amount?: string; reason?: string }>({});
  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={t('adjustments.requestTitle')}
      description={t('adjustments.requestHint')}
      submitLabel={t('adjustments.request')}
      onSubmit={async () => {
        const piastres = parsePounds(amount);
        const found = {
          amount: piastres === null || piastres <= 0 ? t('errors.invalidAmount') : undefined,
          reason: reason.trim() ? undefined : t('errors.required'),
        };
        if (found.amount || found.reason) {
          setErrors(found);
          return false;
        }
        await opsApi.wallets.requestAdjustment(companyId, { amountPiastres: piastres ?? 0, reason: reason.trim() }, idempotencyKey);
        toast(t('adjustments.requested'));
      }}
      onError={(failure) => {
        if (failure.field === 'amountPiastres') setErrors({ amount: failure.message });
        if (failure.field === 'reason') setErrors({ reason: failure.message });
      }}
    >
      <TextField
        label={t('adjustments.amount')}
        inputMode="decimal"
        dir="ltr"
        value={amount}
        error={errors.amount}
        onChange={(event) => {
          setAmount(event.target.value);
          setErrors((previous) => ({ ...previous, amount: undefined }));
        }}
      />
      <TextField
        label={t('common.reason')}
        hint={t('adjustments.reasonHint')}
        maxLength={500}
        value={reason}
        error={errors.reason}
        onChange={(event) => {
          setReason(event.target.value);
          setErrors((previous) => ({ ...previous, reason: undefined }));
        }}
      />
    </FormSheet>
  );
}

/** Cooldown and low-balance alert: Super Admin only, with a reason (the API enforces both). */
function PolicySheet({ company, onClose, onSaved }: { company: Company; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation('live');
  const [cooldown, setCooldown] = useState(String(company.cooldownMinutes));
  const [lowBalance, setLowBalance] = useState(piastresToInput(company.lowBalanceThresholdPiastres));
  const [reason, setReason] = useState('');
  const [errors, setErrors] = useState<{ cooldown?: string; lowBalance?: string; reason?: string }>({});
  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={t('company.policyTitle')}
      description={t('company.policyHint')}
      submitLabel={t('common.save')}
      onSubmit={async () => {
        const minutes = parseWhole(cooldown);
        const piastres = parsePounds(lowBalance || '0');
        const found = {
          cooldown: minutes === null || minutes < 120 || minutes > 240 ? t('errors.outOfRange', { min: 120, max: 240 }) : undefined,
          lowBalance: piastres === null ? t('errors.invalidAmount') : undefined,
          reason: reason.trim() ? undefined : t('errors.required'),
        };
        if (found.cooldown || found.lowBalance || found.reason) {
          setErrors(found);
          return false;
        }
        await opsApi.companies.setPolicy(company.id, {
          cooldownMinutes: minutes ?? 180,
          lowBalanceThresholdPiastres: piastres ?? 0,
          reason: reason.trim(),
        });
        toast(t('company.policySaved'));
        onSaved();
      }}
    >
      <TextField
        label={t('companyForm.cooldown')}
        hint={t('companyForm.cooldownHint')}
        inputMode="numeric"
        dir="ltr"
        value={cooldown}
        error={errors.cooldown}
        onChange={(event) => setCooldown(event.target.value)}
      />
      <TextField
        label={t('companyForm.lowBalance')}
        hint={t('companyForm.lowBalanceHint')}
        inputMode="decimal"
        dir="ltr"
        value={lowBalance}
        error={errors.lowBalance}
        onChange={(event) => setLowBalance(event.target.value)}
      />
      <TextField
        label={t('common.reason')}
        hint={t('common.reasonHint')}
        maxLength={500}
        value={reason}
        error={errors.reason}
        onChange={(event) => setReason(event.target.value)}
      />
    </FormSheet>
  );
}

function DepartmentSheet({ companyId, onClose, onSaved }: { companyId: string; onClose: () => void; onSaved: (department: Department) => void }) {
  const { t } = useTranslation('live');
  const [name, setName] = useState('');
  const [budget, setBudget] = useState('');
  const [errors, setErrors] = useState<{ name?: string; budget?: string }>({});
  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={t('company.addDepartment')}
      submitLabel={t('company.addDepartment')}
      onSubmit={async () => {
        const piastres = optional(budget) ? parsePounds(budget) : undefined;
        const found = {
          name: name.trim() ? undefined : t('errors.required'),
          budget: piastres === null ? t('errors.invalidAmount') : undefined,
        };
        if (found.name || found.budget) {
          setErrors(found);
          return false;
        }
        const created = await opsApi.companies.addDepartment(companyId, { name: name.trim(), monthlyBudgetPiastres: piastres ?? undefined });
        toast(t('company.departmentAdded'));
        onSaved(created);
      }}
      onError={(error) => {
        if (error.field === 'name') setErrors({ name: error.message });
      }}
    >
      <TextField
        label={t('company.departmentName')}
        maxLength={120}
        value={name}
        error={errors.name}
        onChange={(event) => setName(event.target.value)}
      />
      <TextField
        label={t('common.optional', { label: t('company.budget') })}
        hint={t('company.budgetHint')}
        inputMode="decimal"
        dir="ltr"
        value={budget}
        error={errors.budget}
        onChange={(event) => setBudget(event.target.value)}
      />
    </FormSheet>
  );
}
