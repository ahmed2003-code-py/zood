/**
 * Station finance building blocks shared by Ops and the station portal: the payable position, the
 * masked bank account, the statement, and the row every settlement list uses. Amounts are integer
 * piastres. A bank account number is never shown: only its last four characters, as the API sends.
 */
import { ArrowDownLeft, ArrowUpRight, Landmark } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Banner, DateTimeText, ErrorState, Icon, KpiTile, Money, Plate, Skeleton } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatMoney } from '@/lib/format';
import { ListPanel } from '@/shell/page';
import type { Payout, Reversal, StationBankDetails, StationPayable, StationStatementEntry } from './api';
import { PayoutStatusChip, ReversalStatusChip } from './chips';
import { EmptyList, PagedList } from './components';
import type { PagedResource, Resource } from './use-resource';

/** Balance, held, available and (for Operations) pending payouts. */
export function PayableCards({ resource }: { resource: Resource<StationPayable> }) {
  const { t } = useTranslation('live');
  if (resource.error && !resource.data) return <ErrorState onRetry={resource.reload} className="py-6" />;
  const payable = resource.data;
  if (!payable) {
    return (
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4" aria-busy>
        {Array.from({ length: 4 }, (_, index) => (
          <Skeleton key={index} className="h-24" />
        ))}
      </div>
    );
  }
  return (
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
      <KpiTile emphasis label={t('finance.balance')} value={<Money value={payable.balancePiastres} />} hint={t('finance.balanceHint')} />
      <KpiTile label={t('finance.held')} value={<Money value={payable.heldPiastres} />} hint={t('finance.heldHint')} />
      <KpiTile label={t('finance.available')} value={<Money value={payable.availablePiastres} />} hint={t('finance.availableHint')} />
      {payable.pendingPayoutsPiastres !== undefined && payable.pendingPayoutsPiastres !== null && (
        <KpiTile label={t('finance.pendingPayouts')} value={<Money value={payable.pendingPayoutsPiastres} />} hint={t('finance.pendingPayoutsHint')} />
      )}
    </div>
  );
}

/** The verified account (masked) and any change waiting for approval. `actions` sits beside the title. */
export function BankAccountPanel({ resource, actions }: { resource: Resource<StationBankDetails>; actions?: ReactNode }) {
  const { t } = useTranslation('live');
  const [now] = useState(() => Date.now());
  const body = () => {
    if (resource.error && !resource.data) return <ErrorState onRetry={resource.reload} className="py-6" />;
    const details = resource.data;
    if (!details) return <Skeleton className="h-24" />;
    const { active, pending } = details;
    return (
      <div className="flex flex-col gap-3">
        {pending && (
          <Banner
            tone="warning"
            title={t('finance.pendingChange')}
            description={t('finance.pendingChangeHint', { bank: pending.bankName, last4: pending.accountLast4 })}
          />
        )}
        {active ? (
          <div className="flex items-start gap-3 rounded-container border border-border bg-surface p-4">
            <span className="mt-0.5 flex size-10 shrink-0 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
              <Icon icon={Landmark} />
            </span>
            <div className="min-w-0">
              <p className="font-medium">{active.bankName}</p>
              <p className="text-sm text-muted">{active.holderName}</p>
              <p className="mt-1 text-sm">
                {t('finance.accountEnding')} <span dir="ltr" className="font-mono">{active.accountLast4}</span>
              </p>
              <p className="text-xs text-muted">
                {t('finance.letter')} <span dir="ltr">{active.bankLetterReference}</span>
              </p>
              {active.payoutsHeldUntil && new Date(active.payoutsHeldUntil).getTime() > now && (
                <p className="mt-1 text-xs font-medium text-warning">
                  {t('finance.payoutsHeldUntil')} <DateTimeText value={active.payoutsHeldUntil} />
                </p>
              )}
            </div>
          </div>
        ) : (
          <ListPanel>
            <EmptyList title={t('finance.bankNone')} description={t('finance.bankNoneHint')} />
          </ListPanel>
        )}
      </div>
    );
  };
  return (
    <div className="flex flex-col gap-3">
      {actions && <div className="flex flex-wrap gap-2">{actions}</div>}
      {body()}
    </div>
  );
}

/** The station's movements, newest first, each with the payable right after it. */
export function StationStatementList({ resource }: { resource: PagedResource<StationStatementEntry> }) {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  return (
    <PagedList resource={resource} empty={<EmptyList title={t('finance.noEntries')} description={t('finance.noEntriesHint')} />}>
      {(items) => (
        <ul className="divide-y divide-border">
          {items.map((entry) => {
            const credit = entry.direction === 'CREDIT';
            const detail =
              entry.fueling?.reference ?? entry.payout?.bankReference ?? entry.reference ?? entry.description;
            return (
              <li key={entry.id} className="flex items-center gap-3 px-4 py-3">
                <span
                  className={cn(
                    'flex size-9 shrink-0 items-center justify-center rounded-full',
                    credit ? 'bg-success-subtle text-success' : 'bg-surface-sunken text-muted',
                  )}
                >
                  <Icon icon={credit ? ArrowDownLeft : ArrowUpRight} size={18} />
                </span>
                <div className="min-w-0 flex-1">
                  <p className="font-medium">
                    {t(`finance.sources.${entry.sourceType}`)}
                    {entry.fueling?.payableHeld && !entry.fueling.holdReleased && (
                      <span className="ms-2 text-xs font-medium text-warning">{t('finance.entryHeld')}</span>
                    )}
                    {entry.fueling?.holdReleased && <span className="ms-2 text-xs font-medium text-success">{t('finance.entryReleased')}</span>}
                  </p>
                  <p className="truncate text-xs text-muted">
                    <DateTimeText value={entry.postedAt} />
                    {entry.fueling && (
                      <>
                        {' · '}
                        <span dir="ltr">{entry.fueling.plate}</span>
                      </>
                    )}
                    {detail && (
                      <>
                        {' · '}
                        <span dir="ltr">{detail}</span>
                      </>
                    )}
                  </p>
                </div>
                <div className="text-end">
                  <p className={cn('font-semibold tabular-nums', credit && 'text-success')}>
                    <Money value={credit ? entry.amountPiastres : -entry.amountPiastres} signDisplay="always" />
                  </p>
                  <p className="text-xs text-muted tabular-nums">
                    {t('finance.balanceAfter', { amount: formatMoney(entry.balanceAfterPiastres, locale) })}
                  </p>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </PagedList>
  );
}

/**
 * One line of a settlement list (a payout, a bank change, a held payable, a reversal): who, how
 * much, the facts that identify it, its status and the actions that apply.
 */
export function FinanceRow({
  title,
  amount,
  facts,
  notes,
  chip,
  actions,
}: {
  title: ReactNode;
  amount?: number;
  facts?: ReactNode;
  notes?: ReactNode;
  chip: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <div className="flex flex-col gap-2 px-4 py-3">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-sm font-medium">{title}</p>
          {amount !== undefined && (
            <p className="text-lg font-semibold tabular-nums">
              <Money value={amount} />
            </p>
          )}
          {facts && <p className="flex flex-wrap items-center gap-x-2 text-xs text-muted">{facts}</p>}
          {notes}
        </div>
        {chip}
      </div>
      {actions && <div className="flex flex-wrap gap-2">{actions}</div>}
    </div>
  );
}

/** A payout: amount, the bank's reference, the value date and, when it waits for a second person, why. */
export function PayoutRow({ payout, title, actions }: { payout: Payout; title: ReactNode; actions?: ReactNode }) {
  const { t } = useTranslation('live');
  return (
    <FinanceRow
      title={title}
      amount={payout.amountPiastres}
      chip={<PayoutStatusChip status={payout.status} />}
      facts={
        <>
          <span dir="ltr" className="font-mono">
            {payout.bankReference}
          </span>
          <span>{t('settlements.payouts.valueDate', { date: payout.valueDate })}</span>
          {payout.status === 'PENDING' && payout.approvalRequired && <span>{t('settlements.payouts.needsApproval')}</span>}
        </>
      }
      notes={
        <>
          {payout.note && <p className="mt-1 text-sm">{payout.note}</p>}
          {payout.decisionReason && <p className="mt-1 text-sm text-muted">{t('settlements.reason', { reason: payout.decisionReason })}</p>}
        </>
      }
      actions={actions}
    />
  );
}

/** A reversal request (a refund to the company): the fueling, the amount, why, and where it stands. */
export function ReversalRow({ reversal, title, actions }: { reversal: Reversal; title: ReactNode; actions?: ReactNode }) {
  const { t } = useTranslation('live');
  return (
    <FinanceRow
      title={title}
      amount={reversal.amountPiastres}
      chip={<ReversalStatusChip status={reversal.status} />}
      facts={
        <>
          <span dir="ltr" className="font-mono">
            {reversal.fuelingReference}
          </span>
          <Plate value={reversal.plate} size="sm" />
          <DateTimeText value={reversal.requestedAt} />
        </>
      }
      notes={
        <>
          <p className="mt-1 text-sm">{t('settlements.reason', { reason: reversal.reason })}</p>
          {reversal.decisionReason && <p className="mt-1 text-sm text-muted">{t('settlements.decision', { reason: reversal.decisionReason })}</p>}
        </>
      }
      actions={actions}
    />
  );
}
