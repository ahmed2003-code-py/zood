import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorState, SelectField, Skeleton } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { ListPanel, PageHeader, PageSection } from '@/shell/page';
import { stationApi } from '../api';
import { EmptyList, PagedList } from '../components';
import { BankAccountPanel, PayableCards, PayoutRow, ReversalRow, StationStatementList } from '../finance';
import { usePagedResource, useResource } from '../use-resource';

/** One station's money: what it is owed, where it is paid, what was paid and what was refunded. */
function StationFinance({ stationId }: { stationId: string }) {
  const { t } = useTranslation('live');
  const payable = useResource((signal) => stationApi.payable(stationId, signal), [stationId]);
  const bank = useResource((signal) => stationApi.bankAccount(stationId, signal), [stationId]);
  const statement = usePagedResource((page, signal) => stationApi.statement(stationId, page, signal), [stationId]);
  const payouts = usePagedResource((page, signal) => stationApi.payouts({ ...page, stationId }, signal), [stationId], 10);
  const reversals = usePagedResource((page, signal) => stationApi.reversals({ ...page, stationId }, signal), [stationId], 10);

  return (
    <div className="flex flex-col gap-8">
      <PayableCards resource={payable} />
      <div className="grid grid-cols-1 gap-8 lg:grid-cols-5">
        <div className="flex min-w-0 flex-col gap-8 lg:col-span-2">
          <PageSection title={t('finance.bank')}>
            <BankAccountPanel resource={bank} />
          </PageSection>
          <PageSection title={t('stationSettlements.payouts')}>
            <ListPanel>
              <PagedList
                resource={payouts}
                empty={<EmptyList title={t('stationSettlements.noPayouts')} description={t('stationSettlements.noPayoutsHint')} />}
              >
                {(items) => (
                  <div className="divide-y divide-border">
                    {items.map((payout) => (
                      <PayoutRow key={payout.id} payout={payout} title={t('stationSettlements.payoutTitle')} />
                    ))}
                  </div>
                )}
              </PagedList>
            </ListPanel>
          </PageSection>
          <PageSection title={t('stationSettlements.reversals')} description={t('stationSettlements.reversalsHint')}>
            <ListPanel>
              <PagedList
                resource={reversals}
                empty={<EmptyList title={t('stationSettlements.noReversals')} description={t('stationSettlements.noReversalsHint')} />}
              >
                {(items) => (
                  <div className="divide-y divide-border">
                    {items.map((reversal) => (
                      <ReversalRow key={reversal.id} reversal={reversal} title={t('stationSettlements.reversalTitle')} />
                    ))}
                  </div>
                )}
              </PagedList>
            </ListPanel>
          </PageSection>
        </div>
        <PageSection className="lg:col-span-3" title={t('finance.statement')} description={t('finance.statementHint')}>
          <ListPanel>
            <StationStatementList resource={statement} />
          </ListPanel>
        </PageSection>
      </div>
    </div>
  );
}

/**
 * Station portal, read only: choose one of the operator's stations, then see its payable position,
 * statement, verified bank account (last four characters only), payouts and approved reversals.
 */
export function StationSettlementsPage() {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const [chosen, setChosen] = useState<string>();
  const stations = useResource((signal) => stationApi.stations({ limit: 100 }, signal), []);
  const items = stations.data?.items ?? [];
  const stationId = chosen ?? items[0]?.id;

  return (
    <>
      <PageHeader title={t('stationSettlements.title')} description={t('stationSettlements.description')} />
      {stations.error && !stations.data ? (
        <ErrorState onRetry={stations.reload} />
      ) : !stations.data ? (
        <div className="flex flex-col gap-4" aria-busy>
          <Skeleton className="h-16" />
          <Skeleton className="h-40" />
        </div>
      ) : !stationId ? (
        <ListPanel>
          <EmptyList title={t('stationSettlements.noStations')} description={t('stationSettlements.noStationsHint')} />
        </ListPanel>
      ) : (
        <div className="flex flex-col gap-6">
          {items.length > 1 && (
            <SelectField
              className="md:w-72"
              label={t('stationSettlements.station')}
              value={stationId}
              onValueChange={setChosen}
              options={items.map((station) => ({ value: station.id, label: locale === 'ar' ? station.nameAr : station.nameEn }))}
            />
          )}
          <StationFinance key={stationId} stationId={stationId} />
        </div>
      )}
    </>
  );
}
