import { Gauge, Nfc } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';
import { Banner, DateTimeText, EmptyState, ErrorState, ListRow, Money, Plate, Skeleton, Volume } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatDateTime } from '@/lib/format';
import { BackLink, DetailList, ListPanel, PageHeader, PageSection, ViewOnlyNote } from '@/shell/page';
import { companyApi } from '../api';
import { TagStatusChip, VehicleStatusChip } from '../chips';
import { usePagedResource, useResource } from '../use-resource';

/** Left-to-right text inside a translated sentence, isolated so it never reorders the sentence. */
const isolate = (value: string) => `⁨${value}⁩`;

/** Company portal: one of the company's vehicles with its tags and limits, read only. */
export function CompanyVehiclePage() {
  const { id = '' } = useParams();
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const detail = useResource((signal) => companyApi.vehicle(id, signal), [id]);
  const departments = usePagedResource((page, signal) => companyApi.departments(page, signal), [], 100);

  if (detail.error && !detail.data) {
    return (
      <>
        <BackLink to="/company/vehicles" label={t('portal.vehicle.back')} />
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
  const department = departments.items.find((item) => item.id === vehicle.departmentId);

  return (
    <>
      <BackLink to="/company/vehicles" label={t('portal.vehicle.back')} />
      <PageHeader title={`${vehicle.make} ${vehicle.model}`} description={t(`enums.fuel.${vehicle.fuelType}`)} />
      {vehicle.status === 'DECOMMISSIONED' && (
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
                vehicle.year != null && { label: t('vehicleForm.year'), value: vehicle.year },
                { label: t('vehicleForm.department'), value: department?.name ?? t('vehicleForm.noDepartment') },
                { label: t('common.createdAt'), value: <DateTimeText value={vehicle.createdAt} variant="date" /> },
              ]}
            />
          </ListPanel>
        </PageSection>

        <div className="flex min-w-0 flex-col gap-8">
          <PageSection title={t('vehicle.tags')}>
            <ListPanel>
              {tags.length === 0 ? (
                <EmptyState icon={Nfc} title={t('portal.vehicle.noTags')} description={t('portal.vehicle.noTagsHint')} />
              ) : (
                <div className="divide-y divide-border">
                  {tags.map((tag) => (
                    <ListRow
                      key={tag.id}
                      title={t('vehicle.tagEnding', { last4: isolate(tag.uidLast4) })}
                      subtitle={
                        tag.cooldownUntil && new Date(tag.cooldownUntil) > new Date()
                          ? t('vehicle.cooldownUntil', { time: formatDateTime(tag.cooldownUntil, locale) })
                          : (tag.statusReason ?? formatDateTime(tag.createdAt, locale))
                      }
                      trailing={<TagStatusChip status={tag.status} />}
                    />
                  ))}
                </div>
              )}
            </ListPanel>
          </PageSection>

          <PageSection title={t('vehicle.limits')}>
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
                        limit.kind === 'VOLUME' ? (
                          <Volume value={limit.maxValue} fractionDigits={0} />
                        ) : (
                          <Money value={limit.maxValue} precision="whole" />
                        )
                      }
                    />
                  ))}
                </div>
              )}
            </ListPanel>
          </PageSection>
        </div>
      </div>
      <div className="mt-6">
        <ViewOnlyNote />
      </div>
    </>
  );
}
