import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, Money, SegmentedControl, TextField, toast } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatMoney } from '@/lib/format';
import { ListPanel, PageHeader } from '@/shell/page';
import { opsApi, type TopUp, type TopUpStatus } from '../api';
import { EmptyList, FormSheet, PagedList, ReasonSheet } from '../components';
import { optional } from '../input';
import { usePagedResource, useResource } from '../use-resource';
import { TopUpRow } from '../wallet';

type Filter = 'ALL' | TopUpStatus;

/**
 * Ops: bank transfers reported by companies. Each is checked against the bank statement, then
 * confirmed (the wallet is credited at once) or rejected with a reason the company sees.
 */
export function TopUpsPage() {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const [status, setStatus] = useState<Filter>('PENDING');
  const [confirming, setConfirming] = useState<TopUp>();
  const [rejecting, setRejecting] = useState<TopUp>();
  const topUps = usePagedResource(
    (page, signal) => opsApi.topUps.list({ ...page, status: status === 'ALL' ? undefined : status }, signal),
    [status],
  );
  // Names for the companies on screen: the first 100 cover a pilot; beyond that the id links to it.
  const companies = useResource((signal) => opsApi.companies.list({ limit: 100 }, signal), []);
  const companyName = (id: string) => {
    const company = companies.data?.items.find((item) => item.id === id);
    return company ? (locale === 'ar' ? company.nameAr : company.nameEn) : id;
  };

  return (
    <>
      <PageHeader title={t('topUps.title')} description={t('topUps.description')} />
      <SegmentedControl<Filter>
        className="mb-4"
        label={t('topUps.statusFilter')}
        value={status}
        onValueChange={setStatus}
        options={[
          { value: 'PENDING', label: t('topUps.status.PENDING') },
          { value: 'CONFIRMED', label: t('topUps.status.CONFIRMED') },
          { value: 'REJECTED', label: t('topUps.status.REJECTED') },
          { value: 'ALL', label: t('common.all') },
        ]}
      />
      <ListPanel>
        <PagedList resource={topUps} empty={<EmptyList title={t('topUps.empty')} description={t('topUps.emptyHint')} />}>
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((topUp) => (
                <TopUpRow
                  key={topUp.id}
                  topUp={topUp}
                  title={
                    <Link to={`/ops/companies/${topUp.companyId}`} className="font-medium text-foreground hover:underline">
                      {companyName(topUp.companyId)}
                    </Link>
                  }
                  actions={
                    topUp.status === 'PENDING' && (
                      <>
                        <Button size="sm" onClick={() => setConfirming(topUp)}>
                          {t('topUps.confirm')}
                        </Button>
                        <Button size="sm" variant="ghost" onClick={() => setRejecting(topUp)}>
                          {t('topUps.reject')}
                        </Button>
                      </>
                    )
                  }
                />
              ))}
            </div>
          )}
        </PagedList>
      </ListPanel>

      {confirming && (
        <ConfirmSheet
          topUp={confirming}
          title={t('topUps.confirmTitle', {
            amount: formatMoney(confirming.amountPiastres, locale),
            company: companyName(confirming.companyId),
          })}
          onClose={() => setConfirming(undefined)}
          onDone={topUps.reload}
        />
      )}
      <ReasonSheet
        open={Boolean(rejecting)}
        onOpenChange={() => setRejecting(undefined)}
        title={t('topUps.rejectTitle')}
        description={t('topUps.rejectHint')}
        submitLabel={t('topUps.reject')}
        submitVariant="danger"
        onConfirm={async (reason) => {
          if (!rejecting) return;
          await opsApi.topUps.reject(rejecting.id, reason);
          toast(t('topUps.rejected'));
          topUps.reload();
        }}
      />
    </>
  );
}

/** Confirming credits the wallet at once: the sheet repeats the amount and asks for the statement's reference. */
function ConfirmSheet({ topUp, title, onClose, onDone }: { topUp: TopUp; title: string; onClose: () => void; onDone: () => void }) {
  const { t } = useTranslation('live');
  const [reference, setReference] = useState('');
  const [note, setNote] = useState('');
  const [error, setError] = useState<string>();
  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={title}
      description={t('topUps.confirmHint')}
      submitLabel={t('topUps.confirm')}
      onSubmit={async () => {
        await opsApi.topUps.confirm(topUp.id, { bankReference: optional(reference), note: optional(note) });
        toast(t('topUps.confirmed'));
        onDone();
      }}
      onError={(failure) => {
        if (failure.field === 'bankReference') setError(failure.message);
      }}
    >
      <div className="rounded-control border border-border bg-surface-sunken p-3">
        <p className="text-2xl font-semibold tabular-nums">
          <Money value={topUp.amountPiastres} />
        </p>
        <p dir="ltr" className="font-mono text-sm text-muted">
          {topUp.bankReference}
        </p>
      </div>
      <TextField
        label={t('common.optional', { label: t('topUps.correctedReference') })}
        hint={t('topUps.correctedReferenceHint')}
        dir="ltr"
        maxLength={64}
        value={reference}
        error={error}
        onChange={(event) => {
          setReference(event.target.value);
          setError(undefined);
        }}
      />
      <TextField label={t('common.optional', { label: t('topUps.note') })} maxLength={500} value={note} onChange={(event) => setNote(event.target.value)} />
    </FormSheet>
  );
}
