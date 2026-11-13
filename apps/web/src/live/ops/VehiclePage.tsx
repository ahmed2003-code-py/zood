import { Ban, CirclePlay, Gauge, Nfc, Pause, Pencil, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';
import {
  Banner,
  Button,
  DateTimeText,
  EmptyState,
  ErrorState,
  IconButton,
  ListRow,
  Money,
  Plate,
  SegmentedControl,
  Skeleton,
  TextField,
  toast,
  Volume,
} from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatDateTime } from '@/lib/format';
import { BackLink, DetailList, ListPanel, PageHeader, PageSection } from '@/shell/page';
import { opsApi, type Limit, type LimitKind, type LimitPeriod, type Tag, type TagStatus, type VehicleStatus } from '../api';
import { TagStatusChip, VehicleStatusChip } from '../chips';
import { FormSheet, ReasonSheet } from '../components';
import { parseLitres, parsePounds } from '../input';
import { usePagedResource, useResource } from '../use-resource';
import { VehicleFormSheet } from './vehicle-form';

/** Left-to-right text (a UID) inside a translated sentence, isolated so it never reorders the sentence. */
const isolate = (value: string) => `⁨${value}⁩`;

/** What a tag can become from each status (the API enforces the same table). */
const TAG_ACTIONS: Record<TagStatus, TagStatus[]> = {
  ACTIVE: ['REPLACEMENT_PENDING', 'LOST', 'STOLEN', 'DEACTIVATED'],
  REPLACEMENT_PENDING: ['ACTIVE', 'LOST', 'STOLEN', 'DEACTIVATED'],
  LOST: [],
  STOLEN: [],
  DEACTIVATED: [],
};

type Dialog =
  | { kind: 'edit' }
  | { kind: 'status'; to: VehicleStatus }
  | { kind: 'tag' }
  | { kind: 'tagStatus'; tag: Tag; to: TagStatus }
  | { kind: 'limit' }
  | { kind: 'removeLimit'; limit: Limit }
  | undefined;

/** Ops: one vehicle with its RFID tags and limits. */
export function VehiclePage() {
  const { id = '' } = useParams();
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const [dialog, setDialog] = useState<Dialog>();
  const detail = useResource((signal) => opsApi.vehicles.get(id, signal), [id]);
  const companyId = detail.data?.vehicle.companyId;
  const departments = usePagedResource(
    (page, signal) => (companyId ? opsApi.companies.departments(companyId, page, signal) : Promise.resolve({ items: [] })),
    [companyId],
    100,
  );
  const close = () => setDialog(undefined);

  if (detail.error && !detail.data) {
    return (
      <>
        <BackLink to="/ops/companies" label={t('company.back')} />
        <ErrorState onRetry={detail.reload} />
      </>
    );
  }
  if (!detail.data) {
    return (
      <div className="flex flex-col gap-4" aria-busy>
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  const { vehicle, tags, limits } = detail.data;
  const retired = vehicle.status === 'DECOMMISSIONED';
  const department = departments.items.find((item) => item.id === vehicle.departmentId);
  const activeTag = tags.find((tag) => tag.status === 'ACTIVE');

  return (
    <>
      <BackLink to={`/ops/companies/${vehicle.companyId}`} label={t('company.vehicles')} />
      <PageHeader
        title={`${vehicle.make} ${vehicle.model}`}
        description={t(`enums.fuel.${vehicle.fuelType}`)}
        actions={
          !retired && (
            <>
              <Button variant="secondary" leadingIcon={Pencil} onClick={() => setDialog({ kind: 'edit' })}>
                {t('common.edit')}
              </Button>
              {vehicle.status === 'ACTIVE' ? (
                <Button variant="secondary" leadingIcon={Pause} onClick={() => setDialog({ kind: 'status', to: 'SUSPENDED' })}>
                  {t('vehicle.suspend')}
                </Button>
              ) : (
                <Button leadingIcon={CirclePlay} onClick={() => setDialog({ kind: 'status', to: 'ACTIVE' })}>
                  {t('vehicle.reactivate')}
                </Button>
              )}
              <Button variant="ghost" leadingIcon={Ban} onClick={() => setDialog({ kind: 'status', to: 'DECOMMISSIONED' })}>
                {t('vehicle.decommission')}
              </Button>
            </>
          )
        }
      />
      {retired && (
        <Banner tone="info" className="mb-6" title={t('vehicle.decommissionedBanner')} description={t('vehicle.decommissionedBannerHint')} />
      )}

      <div className="grid grid-cols-1 gap-8 lg:grid-cols-2">
        <PageSection title={t('vehicle.details')}>
          <ListPanel>
            <div className="flex justify-center border-b border-border p-5">
              <Plate value={vehicle.plate} size="lg" />
            </div>
            <DetailList
              rows={[
                { label: t('vehicle.status'), value: <VehicleStatusChip status={vehicle.status} /> },
                { label: t('vehicleForm.tier'), value: t(`enums.tier.${vehicle.tier}`) },
                { label: t('vehicle.tank'), value: <Volume value={vehicle.tankCapacityMl} fractionDigits={0} /> },
                vehicle.tier === 'VIP' && {
                  label: t('vehicle.vipCeiling'),
                  value: vehicle.vipMonthlyCeilingMl != null ? <Volume value={vehicle.vipMonthlyCeilingMl} fractionDigits={0} /> : t('common.notSet'),
                },
                vehicle.year != null && { label: t('vehicleForm.year'), value: vehicle.year },
                { label: t('vehicleForm.department'), value: department?.name ?? t('vehicleForm.noDepartment') },
                { label: t('common.createdAt'), value: <DateTimeText value={vehicle.createdAt} variant="date" /> },
              ]}
            />
          </ListPanel>
        </PageSection>

        <div className="flex min-w-0 flex-col gap-8">
          <PageSection
            title={t('vehicle.tags')}
            action={
              !retired &&
              !activeTag && (
                <Button variant="ghost" size="sm" leadingIcon={Plus} onClick={() => setDialog({ kind: 'tag' })}>
                  {t('vehicle.issueTag')}
                </Button>
              )
            }
          >
            <ListPanel>
              {tags.length === 0 ? (
                <EmptyState
                  icon={Nfc}
                  title={t('vehicle.noTags')}
                  description={t('vehicle.noTagsHint')}
                  action={
                    !retired && (
                      <Button leadingIcon={Plus} onClick={() => setDialog({ kind: 'tag' })}>
                        {t('vehicle.issueTag')}
                      </Button>
                    )
                  }
                />
              ) : (
                <div className="divide-y divide-border">
                  {tags.map((tag) => (
                    <div key={tag.id} className="flex flex-col gap-2 px-4 py-3">
                      <div className="flex items-center justify-between gap-3">
                        <div className="min-w-0">
                          <p className="font-medium">{t('vehicle.tagEnding', { last4: isolate(tag.uidLast4) })}</p>
                          <p className="text-xs text-muted">
                            {tag.cooldownUntil && new Date(tag.cooldownUntil) > new Date()
                              ? t('vehicle.cooldownUntil', { time: formatDateTime(tag.cooldownUntil, locale) })
                              : tag.statusReason ?? formatDateTime(tag.createdAt, locale)}
                          </p>
                        </div>
                        <TagStatusChip status={tag.status} />
                      </div>
                      {!retired && TAG_ACTIONS[tag.status].length > 0 && (
                        <div className="flex flex-wrap gap-2">
                          {TAG_ACTIONS[tag.status].map((to) => (
                            <Button
                              key={to}
                              size="sm"
                              variant={to === 'STOLEN' || to === 'LOST' ? 'secondary' : 'ghost'}
                              onClick={() => setDialog({ kind: 'tagStatus', tag, to })}
                            >
                              {t(`vehicle.tagActions.${to}`)}
                            </Button>
                          ))}
                        </div>
                      )}
                    </div>
                  ))}
                </div>
              )}
            </ListPanel>
          </PageSection>

          <PageSection
            title={t('vehicle.limits')}
            action={
              !retired && (
                <Button variant="ghost" size="sm" leadingIcon={Plus} onClick={() => setDialog({ kind: 'limit' })}>
                  {t('vehicle.setLimit')}
                </Button>
              )
            }
          >
            <ListPanel>
              {limits.length === 0 ? (
                <EmptyState icon={Gauge} title={t('vehicle.noLimits')} description={t('vehicle.noLimitsHint')} />
              ) : (
                <div className="divide-y divide-border">
                  {limits.map((limit) => (
                    <ListRow
                      key={limit.id}
                      title={t(`enums.limitPeriod.${limit.period}`)}
                      subtitle={t(`enums.limitKind.${limit.kind}`)}
                      trailing={
                        <span className="flex items-center gap-1">
                          {limit.kind === 'VOLUME' ? (
                            <Volume value={limit.maxValue} fractionDigits={0} />
                          ) : (
                            <Money value={limit.maxValue} precision="whole" />
                          )}
                          {!retired && (
                            <IconButton icon={Trash2} label={t('vehicle.removeLimit')} onClick={() => setDialog({ kind: 'removeLimit', limit })} />
                          )}
                        </span>
                      }
                    />
                  ))}
                </div>
              )}
            </ListPanel>
          </PageSection>
        </div>
      </div>

      {dialog?.kind === 'edit' && (
        <VehicleFormSheet
          open
          onOpenChange={close}
          companyId={vehicle.companyId}
          departments={departments.items}
          vehicle={vehicle}
          onSaved={detail.reload}
        />
      )}
      <ReasonSheet
        open={dialog?.kind === 'status'}
        onOpenChange={close}
        title={
          dialog?.kind === 'status'
            ? t(
                dialog.to === 'SUSPENDED' ? 'vehicle.suspendTitle' : dialog.to === 'ACTIVE' ? 'vehicle.reactivateTitle' : 'vehicle.decommissionTitle',
                { plate: vehicle.plate },
              )
            : ''
        }
        description={
          dialog?.kind === 'status'
            ? t(dialog.to === 'SUSPENDED' ? 'vehicle.suspendHint' : dialog.to === 'ACTIVE' ? 'vehicle.reactivateHint' : 'vehicle.decommissionHint')
            : undefined
        }
        submitLabel={
          dialog?.kind === 'status'
            ? t(dialog.to === 'SUSPENDED' ? 'vehicle.suspend' : dialog.to === 'ACTIVE' ? 'vehicle.reactivate' : 'vehicle.decommission')
            : ''
        }
        submitVariant={dialog?.kind === 'status' && dialog.to !== 'ACTIVE' ? 'danger' : 'primary'}
        onConfirm={async (reason) => {
          if (dialog?.kind !== 'status') return;
          await opsApi.vehicles.setStatus(id, { status: dialog.to, reason });
          toast(t('vehicle.statusChanged'));
          detail.reload();
        }}
      />
      <ReasonSheet
        open={dialog?.kind === 'tagStatus'}
        onOpenChange={close}
        title={
          dialog?.kind === 'tagStatus'
            ? t('vehicle.tagActionTitle', { action: t(`vehicle.tagActions.${dialog.to}`), last4: isolate(dialog.tag.uidLast4) })
            : ''
        }
        description={
          dialog?.kind === 'tagStatus'
            ? t(
                dialog.to === 'REPLACEMENT_PENDING'
                  ? 'vehicle.tagReplacementHint'
                  : dialog.to === 'ACTIVE'
                    ? 'vehicle.tagReactivateHint'
                    : 'vehicle.tagFinalHint',
              )
            : undefined
        }
        submitLabel={dialog?.kind === 'tagStatus' ? t(`vehicle.tagActions.${dialog.to}`) : ''}
        submitVariant={dialog?.kind === 'tagStatus' && dialog.to !== 'ACTIVE' && dialog.to !== 'REPLACEMENT_PENDING' ? 'danger' : 'primary'}
        onConfirm={async (reason) => {
          if (dialog?.kind !== 'tagStatus') return;
          await opsApi.vehicles.setTagStatus(dialog.tag.id, { status: dialog.to, reason });
          toast(t('vehicle.tagUpdated'));
          detail.reload();
        }}
      />
      {dialog?.kind === 'tag' && <IssueTagSheet vehicleId={id} onClose={close} onSaved={detail.reload} />}
      {dialog?.kind === 'limit' && <LimitSheet vehicleId={id} onClose={close} onSaved={detail.reload} />}
      <FormSheet
        open={dialog?.kind === 'removeLimit'}
        onOpenChange={close}
        title={t('vehicle.removeLimitTitle')}
        description={t('vehicle.removeLimitHint')}
        submitLabel={t('vehicle.removeLimit')}
        submitVariant="danger"
        onSubmit={async () => {
          if (dialog?.kind !== 'removeLimit') return;
          await opsApi.vehicles.removeLimit(id, dialog.limit.id);
          toast(t('vehicle.limitRemoved'));
          detail.reload();
        }}
      >
        {dialog?.kind === 'removeLimit' && (
          <p className="text-sm">
            {t(`enums.limitPeriod.${dialog.limit.period}`)} · {t(`enums.limitKind.${dialog.limit.kind}`)}
          </p>
        )}
      </FormSheet>
    </>
  );
}

function IssueTagSheet({ vehicleId, onClose, onSaved }: { vehicleId: string; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation('live');
  const [uid, setUid] = useState('');
  const [error, setError] = useState<string>();
  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={t('vehicle.issueTag')}
      description={t('vehicle.issueTagHint')}
      submitLabel={t('vehicle.issueTag')}
      onSubmit={async () => {
        if (!uid.trim()) {
          setError(t('errors.required'));
          return false;
        }
        await opsApi.vehicles.issueTag(vehicleId, uid.trim());
        toast(t('vehicle.tagIssued'));
        onSaved();
      }}
      onError={(failure) => {
        if (failure.field === 'uid') setError(failure.message);
      }}
    >
      <TextField
        label={t('vehicle.uid')}
        value={uid}
        maxLength={64}
        dir="ltr"
        autoComplete="off"
        autoCapitalize="characters"
        spellCheck={false}
        className="font-mono"
        error={error}
        onChange={(event) => {
          setUid(event.target.value);
          setError(undefined);
        }}
      />
    </FormSheet>
  );
}

function LimitSheet({ vehicleId, onClose, onSaved }: { vehicleId: string; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation('live');
  const [kind, setKind] = useState<LimitKind>('VOLUME');
  const [period, setPeriod] = useState<LimitPeriod>('DAY');
  const [value, setValue] = useState('');
  const [error, setError] = useState<string>();
  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={t('vehicle.setLimit')}
      description={t('vehicle.limitHint')}
      submitLabel={t('vehicle.setLimit')}
      onSubmit={async () => {
        const max = kind === 'VOLUME' ? parseLitres(value) : parsePounds(value);
        if (max === null || max <= 0) {
          setError(t(kind === 'VOLUME' ? 'errors.invalidVolume' : 'errors.invalidAmount'));
          return false;
        }
        await opsApi.vehicles.setLimit(vehicleId, { kind, period, maxValue: max });
        toast(t('vehicle.limitSet'));
        onSaved();
      }}
    >
      <div className="flex flex-col gap-1.5">
        <span className="text-sm font-medium">{t('vehicle.limitKind')}</span>
        <SegmentedControl<LimitKind>
          label={t('vehicle.limitKind')}
          value={kind}
          onValueChange={(next) => {
            setKind(next);
            setError(undefined);
          }}
          options={[
            { value: 'VOLUME', label: t('enums.limitKind.VOLUME') },
            { value: 'AMOUNT', label: t('enums.limitKind.AMOUNT') },
          ]}
        />
      </div>
      <div className="flex flex-col gap-1.5">
        <span className="text-sm font-medium">{t('vehicle.limitPeriod')}</span>
        <SegmentedControl<LimitPeriod>
          label={t('vehicle.limitPeriod')}
          value={period}
          onValueChange={setPeriod}
          options={[
            { value: 'DAY', label: t('enums.limitPeriod.DAY') },
            { value: 'WEEK', label: t('enums.limitPeriod.WEEK') },
            { value: 'MONTH', label: t('enums.limitPeriod.MONTH') },
          ]}
        />
      </div>
      <TextField
        label={t(kind === 'VOLUME' ? 'vehicle.limitValueVolume' : 'vehicle.limitValueAmount')}
        inputMode="decimal"
        dir="ltr"
        value={value}
        error={error}
        onChange={(event) => {
          setValue(event.target.value);
          setError(undefined);
        }}
      />
    </FormSheet>
  );
}
