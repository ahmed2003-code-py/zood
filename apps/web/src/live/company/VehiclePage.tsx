import { Fuel, Gauge, LifeBuoy, Nfc } from 'lucide-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router';
import { Banner, Button, DateTimeText, EmptyState, ErrorState, Icon, KpiTile, ListRow, Money, Plate, Skeleton, Volume } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatDateTime, formatNumber } from '@/lib/format';
import { cairoDay, periodRange } from '@/lib/periods';
import { BackLink, DetailList, ListPanel, PageHeader, PageSection, ViewOnlyNote } from '@/shell/page';
import { companyApi } from '../api';
import { FuelingStatusChip, TagStatusChip, VehicleStatusChip } from '../chips';
import { EmptyList, PagedList } from '../components';
import { LimitUsageRow } from '../insights/LimitUsageRow';
import { limitUsage, sumActivity } from '../insights/model';
import { useCompanyFuelings } from '../insights/use-company-data';
import { usePagedResource, useResource } from '../use-resource';

/** Left-to-right text inside a translated sentence, isolated so it never reorders the sentence. */
const isolate = (value: string) => `⁨${value}⁩`;

/** Company portal: one of the company's vehicles with its tags and limits, read only. */
export function CompanyVehiclePage() {
  const { id = '' } = useParams();
  const { t } = useTranslation(['live', 'portal']);
  const { locale } = useLocale();
  const navigate = useNavigate();
  const detail = useResource((signal) => companyApi.vehicle(id, signal), [id]);
  const departments = usePagedResource((page, signal) => companyApi.departments(page, signal), [], 100);
  const now = useMemo(() => new Date(), []);
  // Limits run by Cairo day, week (from Saturday) and month: read from whichever started first.
  const usageWindow = useMemo(() => {
    const week = periodRange('THIS_WEEK', now);
    const month = periodRange('THIS_MONTH', now);
    return { from: week.from < month.from ? week.from : month.from, to: cairoDay(now) };
  }, [now]);
  const month = useMemo(() => periodRange('THIS_MONTH', now), [now]);
  const recentFuelings = useCompanyFuelings(usageWindow, id);
  const monthActivity = useResource((signal) => companyApi.activity({ from: month.from, to: month.to, vehicleId: id }, signal), [id, month.from]);
  const history = usePagedResource((page, signal) => companyApi.fuelings({ ...page, vehicleId: id }, signal), [id], 10);

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
      <PageHeader
        title={`${vehicle.make} ${vehicle.model}`}
        description={t(`enums.fuel.${vehicle.fuelType}`)}
        badge={<VehicleStatusChip status={vehicle.status} />}
        actions={
          <Button asChild variant="secondary">
            <Link to={`/company/tickets?new=1&vehicleId=${vehicle.id}`}>
              <Icon icon={LifeBuoy} size={18} />
              {t('portal:vehicle.report')}
            </Link>
          </Button>
        }
      />
      {vehicle.status === 'DECOMMISSIONED' && (
        <Banner tone="info" className="mb-6" title={t('vehicle.decommissionedBanner')} description={t('vehicle.decommissionedBannerHint')} />
      )}

      <section aria-label={t('portal:vehicle.month')} className="mb-6 grid grid-cols-2 gap-3 md:mb-8 md:gap-4 lg:grid-cols-3">
        {monthActivity.data ? (
          <>
            <KpiTile emphasis label={t('portal:vehicle.monthSpend')} value={<Money value={sumActivity(monthActivity.data).amountPiastres} />} />
            <KpiTile label={t('portal:vehicle.monthLitres')} value={<Volume value={sumActivity(monthActivity.data).volumeMl} fractionDigits={0} />} />
            <KpiTile
              className="col-span-2 lg:col-span-1"
              label={t('portal:vehicle.monthFuelings')}
              value={formatNumber(sumActivity(monthActivity.data).fuelings, locale)}
            />
          </>
        ) : (
          [0, 1, 2].map((index) => <Skeleton key={index} className={index === 2 ? 'col-span-2 h-28 lg:col-span-1' : 'h-28'} />)
        )}
      </section>

      <div className="grid grid-cols-1 gap-8 lg:grid-cols-2">
        <PageSection title={t('vehicle.details')}>
          <ListPanel>
            <div className="flex justify-center border-b border-border p-5">
              <Plate value={vehicle.plate} size="lg" />
            </div>
            <DetailList
              rows={[
                { label: t('vehicleForm.fleetCode'), value: <span dir="ltr">{vehicle.fleetCode ?? t('common.notSet')}</span> },
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

          <PageSection title={t('vehicle.limits')} description={t('portal:vehicle.limitsHint')}>
            <ListPanel>
              {limits.length === 0 ? (
                <EmptyState
                  icon={Gauge}
                  title={t('vehicle.noLimits')}
                  description={t('vehicle.noLimitsHint')}
                  action={
                    <Button asChild variant="secondary" size="sm">
                      <Link to={`/company/tickets?new=1&type=LIMIT_CHANGE&vehicleId=${vehicle.id}`}>{t('portal:limits.ask')}</Link>
                    </Button>
                  }
                />
              ) : !recentFuelings.data ? (
                <div className="flex flex-col gap-3 p-4" aria-busy>
                  {limits.map((limit) => (
                    <Skeleton key={limit.id} className="h-14" />
                  ))}
                </div>
              ) : (
                <div className="flex flex-col divide-y divide-border">
                  {limitUsage(limits, recentFuelings.data.items, now).map((usage) => (
                    <div key={usage.limit.id} className="px-4 py-3.5 md:px-5">
                      <LimitUsageRow usage={usage} />
                    </div>
                  ))}
                </div>
              )}
            </ListPanel>
          </PageSection>
        </div>
      </div>
      <PageSection className="mt-8" title={t('portal:vehicle.history')} description={t('portal:vehicle.historyHint')}>
        <ListPanel>
          <PagedList resource={history} empty={<EmptyList title={t('portal:vehicle.noHistory')} description={t('portal:vehicle.noHistoryHint')} />}>
            {(items) => (
              <div className="divide-y divide-border">
                {items.map((fueling) => (
                  <ListRow
                    key={fueling.id}
                    leading={
                      <span className="flex size-9 items-center justify-center rounded-control bg-primary-subtle text-primary-subtle-foreground">
                        <Icon icon={Fuel} size={18} />
                      </span>
                    }
                    title={locale === 'ar' ? fueling.stationNameAr : fueling.stationNameEn}
                    subtitle={
                      <>
                        <DateTimeText value={fueling.evaluatedAt} />
                        {' · '}
                        <Volume value={fueling.volumeMl} />
                      </>
                    }
                    trailing={
                      fueling.status === 'COMPLETED' ? (
                        fueling.billable ? <Money value={fueling.amountPiastres} /> : <span className="text-sm text-muted">{t('fuelings.notBilled')}</span>
                      ) : (
                        <FuelingStatusChip status={fueling.status} declineReason={fueling.declineReason} />
                      )
                    }
                    onClick={() => navigate(`/company/transactions/${fueling.id}`)}
                  />
                ))}
              </div>
            )}
          </PagedList>
        </ListPanel>
      </PageSection>
      <div className="mt-6">
        <ViewOnlyNote />
      </div>
    </>
  );
}
