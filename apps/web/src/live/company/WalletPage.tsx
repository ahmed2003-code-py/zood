import { Landmark } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, toast } from '@/design-system';
import { ListPanel, PageHeader, PageSection } from '@/shell/page';
import { companyApi, type TopUp } from '../api';
import { EmptyList, FormSheet, PagedList } from '../components';
import { usePagedResource, useResource } from '../use-resource';
import { BalanceCard, StatementList, TopUpRow, TransferSheet } from '../wallet';

/**
 * The company's wallet: the balance, the low-balance warning, its bank-transfer top-up requests and
 * the statement. Both company roles may report a transfer; only Operations credits it.
 */
export function CompanyWalletPage() {
  const { t } = useTranslation('live');
  const [requesting, setRequesting] = useState(false);
  const [cancelling, setCancelling] = useState<TopUp>();
  const wallet = useResource((signal) => companyApi.wallet(signal), []);
  const topUps = usePagedResource((page, signal) => companyApi.topUps(page, signal), [], 10);
  const statement = usePagedResource((page, signal) => companyApi.statement(page, signal), []);
  const refresh = () => {
    wallet.reload();
    topUps.reload();
    statement.reload();
  };

  return (
    <>
      <PageHeader title={t('wallet.title')} description={t('wallet.description')} />
      <div className="grid grid-cols-1 gap-8 lg:grid-cols-5">
        <div className="flex min-w-0 flex-col gap-8 lg:col-span-2">
          <BalanceCard
            wallet={wallet.data}
            loading={wallet.loading}
            action={
              <Button variant="secondary" leadingIcon={Landmark} onClick={() => setRequesting(true)}>
                {t('wallet.requestTopUp')}
              </Button>
            }
          />
          <PageSection title={t('topUps.mine')}>
            <p className="-mt-1 text-sm text-muted">{t('topUps.minesHint')}</p>
            <ListPanel>
              <PagedList
                resource={topUps}
                empty={<EmptyList title={t('topUps.emptyMine')} description={t('topUps.emptyMineHint')} />}
              >
                {(items) => (
                  <div className="divide-y divide-border">
                    {items.map((topUp) => (
                      <TopUpRow
                        key={topUp.id}
                        topUp={topUp}
                        actions={
                          topUp.status === 'PENDING' && (
                            <Button size="sm" variant="ghost" onClick={() => setCancelling(topUp)}>
                              {t('topUps.cancel')}
                            </Button>
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
        <PageSection className="lg:col-span-3" title={t('wallet.statement')}>
          <p className="-mt-1 text-sm text-muted">{t('wallet.statementHint')}</p>
          <ListPanel>
            <StatementList resource={statement} />
          </ListPanel>
        </PageSection>
      </div>

      {requesting && (
        <TransferSheet
          title={t('topUps.requestTitle')}
          description={t('topUps.requestHint')}
          submitLabel={t('topUps.submit')}
          doneMessage={t('topUps.requested')}
          send={(body) => companyApi.requestTopUp(body)}
          onClose={() => setRequesting(false)}
          onDone={topUps.reload}
        />
      )}
      <FormSheet
        open={Boolean(cancelling)}
        onOpenChange={() => setCancelling(undefined)}
        title={t('topUps.cancelTitle')}
        description={t('topUps.cancelHint')}
        submitLabel={t('topUps.cancel')}
        submitVariant="danger"
        onSubmit={async () => {
          if (!cancelling) return;
          await companyApi.cancelTopUp(cancelling.id);
          toast(t('topUps.cancelled'));
          refresh();
        }}
      >
        {cancelling && <TopUpRow topUp={cancelling} />}
      </FormSheet>
    </>
  );
}
