import { CalendarClock, Plus } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, DataView, DateTimeText, ErrorState, ListRow, Money, Skeleton, StatusChip, type DataColumn } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { ListPanel, PageHeader, PageSection } from '@/shell/page';
import { opsApi, type PriceEntry } from '../api';
import { EmptyList, PagedList } from '../components';
import { usePagedResource, useResource } from '../use-resource';
import { PriceSheet } from './price-form';

/** Ops: the national fuel prices in force, and the history and schedule behind them. */
export function PricesPage() {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const [scheduling, setScheduling] = useState(false);
  const current = useResource((signal) => opsApi.prices.current(undefined, signal), []);
  const history = usePagedResource((page, signal) => opsApi.prices.history(page, signal), []);
  const stations = useResource((signal) => opsApi.stations.list({ limit: 100 }, signal), []);

  const stationName = (id?: string) => {
    if (!id) return t('prices.national');
    const station = stations.data?.items.find((item) => item.id === id);
    return station ? (locale === 'ar' ? station.nameAr : station.nameEn) : t('prices.stationOverride');
  };
  const isScheduled = (entry: PriceEntry) => new Date(entry.effectiveFrom) > new Date();
  const reload = () => {
    current.reload();
    history.reload();
  };

  const columns: DataColumn<PriceEntry>[] = [
    { id: 'fuel', header: t('prices.fuel'), cell: (entry) => <span className="font-medium">{t(`enums.fuel.${entry.fuelType}`)}</span> },
    { id: 'place', header: t('prices.place'), cell: (entry) => stationName(entry.stationId) },
    { id: 'price', header: t('prices.perLitre'), align: 'end', cell: (entry) => <Money value={entry.pricePiastresPerLitre} precision="exact" /> },
    {
      id: 'from',
      header: t('prices.effectiveFrom'),
      cell: (entry) => (
        <span className="inline-flex flex-wrap items-center gap-2">
          <DateTimeText value={entry.effectiveFrom} />
          {isScheduled(entry) && <StatusChip tone="info" icon={CalendarClock} label={t('prices.scheduledChip')} />}
        </span>
      ),
    },
    { id: 'note', header: t('prices.note'), cell: (entry) => entry.note ?? t('common.notSet') },
  ];

  return (
    <>
      <PageHeader
        title={t('prices.title')}
        description={t('prices.description')}
        actions={
          <Button leadingIcon={Plus} onClick={() => setScheduling(true)}>
            {t('prices.schedule')}
          </Button>
        }
      />

      <div className="flex flex-col gap-8">
        <PageSection title={t('prices.now')}>
          <ListPanel>
            {current.error && !current.data ? (
              <ErrorState onRetry={current.reload} />
            ) : !current.data ? (
              <div className="flex flex-col gap-3 p-4" aria-busy>
                {Array.from({ length: 3 }, (_, index) => (
                  <Skeleton key={index} className="h-10" />
                ))}
              </div>
            ) : current.data.length === 0 ? (
              <EmptyList title={t('prices.noneNow')} description={t('prices.noneNowHint')} />
            ) : (
              <div className="divide-y divide-border">
                {current.data.map((price) => (
                  <ListRow
                    key={`${price.fuelType}-${price.stationId ?? 'national'}`}
                    title={t(`enums.fuel.${price.fuelType}`)}
                    subtitle={
                      <span className="inline-flex flex-wrap gap-1">
                        {t('prices.since')}
                        <DateTimeText value={price.effectiveFrom} />
                      </span>
                    }
                    trailing={
                      <span className="inline-flex items-baseline gap-1">
                        <Money value={price.pricePiastresPerLitre} precision="exact" />
                        <span className="text-xs font-normal text-muted">{t('prices.perLitreShort')}</span>
                      </span>
                    }
                  />
                ))}
              </div>
            )}
          </ListPanel>
        </PageSection>

        <PageSection title={t('prices.history')}>
          <ListPanel>
            <PagedList
              resource={history}
              empty={<EmptyList title={t('prices.empty')} description={t('prices.emptyHint')} />}
            >
              {(items) => (
                <DataView
                  caption={t('prices.history')}
                  rows={items}
                  columns={columns}
                  getRowId={(entry) => entry.id}
                  tableFrom="md"
                  renderListItem={(entry) => (
                    <ListRow
                      title={`${t(`enums.fuel.${entry.fuelType}`)} · ${stationName(entry.stationId)}`}
                      subtitle={
                        <span className="inline-flex flex-wrap items-center gap-2">
                          <DateTimeText value={entry.effectiveFrom} />
                          {isScheduled(entry) && <StatusChip tone="info" icon={CalendarClock} label={t('prices.scheduledChip')} />}
                        </span>
                      }
                      trailing={<Money value={entry.pricePiastresPerLitre} precision="exact" />}
                    />
                  )}
                />
              )}
            </PagedList>
          </ListPanel>
        </PageSection>
      </div>

      {scheduling && <PriceSheet stations={stations.data?.items ?? []} onClose={() => setScheduling(false)} onSaved={reload} />}
    </>
  );
}
