import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, DateTimeText, Plate, SegmentedControl, Tabs, toast } from '@/design-system';
import { ListPanel, PageHeader } from '@/shell/page';
import {
  opsApi,
  type BankAccount,
  type BankAccountStatus,
  type Fueling,
  type HeldPayable,
  type HoldState,
  type Payout,
  type PayoutStatus,
  type Reversal,
  type ReversalStatus,
} from '../api';
import { BankChangeStatusChip, HoldStateChip } from '../chips';
import { EmptyList, FormSheet, PagedList, ReasonSheet } from '../components';
import { FinanceRow, PayoutRow, ReversalRow } from '../finance';
import { usePagedResource, useStationNames } from '../use-resource';

/** A decision that needs no reason (approve): the sheet shows what is being decided and asks once more. */
function ConfirmSheet({
  title,
  description,
  submitLabel,
  onConfirm,
  onClose,
  children,
}: {
  title: string;
  description: string;
  submitLabel: string;
  onConfirm: () => Promise<void>;
  onClose: () => void;
  children: ReactNode;
}) {
  return (
    <FormSheet open onOpenChange={onClose} title={title} description={description} submitLabel={submitLabel} onSubmit={onConfirm}>
      <div className="overflow-hidden rounded-container border border-border bg-surface">{children}</div>
    </FormSheet>
  );
}

/** The station's name as a link to its page, or its id when the stations list has not loaded it. */
function StationLink({ id, name }: { id: string; name?: string }) {
  const { t } = useTranslation('live');
  return (
    <Link to={`/ops/stations/${id}`} className="text-primary hover:underline">
      {name ?? t('settlements.openStation')}
    </Link>
  );
}

type PayoutFilter = 'ALL' | PayoutStatus;

function PayoutsTab() {
  const { t } = useTranslation('live');
  const [status, setStatus] = useState<PayoutFilter>('PENDING');
  const [approving, setApproving] = useState<Payout>();
  const [cancelling, setCancelling] = useState<Payout>();
  const stationName = useStationNames((signal) => opsApi.stations.list({ limit: 100 }, signal));
  const payouts = usePagedResource(
    (page, signal) => opsApi.finance.payouts({ ...page, status: status === 'ALL' ? undefined : status }, signal),
    [status],
  );
  return (
    <>
      <SegmentedControl<PayoutFilter>
        className="mb-4"
        label={t('settlements.statusFilter')}
        value={status}
        onValueChange={setStatus}
        options={[
          { value: 'PENDING', label: t('settlements.payouts.status.PENDING') },
          { value: 'POSTED', label: t('settlements.payouts.status.POSTED') },
          { value: 'CANCELLED', label: t('settlements.payouts.status.CANCELLED') },
          { value: 'ALL', label: t('common.all') },
        ]}
      />
      <ListPanel>
        <PagedList resource={payouts} empty={<EmptyList title={t('settlements.payouts.empty')} description={t('settlements.payouts.emptyHint')} />}>
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((payout) => (
                <PayoutRow
                  key={payout.id}
                  payout={payout}
                  title={<StationLink id={payout.stationId} name={stationName(payout.stationId)} />}
                  actions={
                    payout.status === 'PENDING' && (
                      <>
                        <Button size="sm" onClick={() => setApproving(payout)}>
                          {t('settlements.payouts.approve')}
                        </Button>
                        <Button size="sm" variant="ghost" onClick={() => setCancelling(payout)}>
                          {t('settlements.payouts.cancel')}
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
      {approving && (
        <ConfirmSheet
          title={t('settlements.payouts.approveTitle')}
          description={t('settlements.payouts.approveHint')}
          submitLabel={t('settlements.payouts.approve')}
          onClose={() => setApproving(undefined)}
          onConfirm={async () => {
            await opsApi.finance.approvePayout(approving.id);
            toast(t('settlements.payouts.approved'));
            payouts.reload();
          }}
        >
          <PayoutRow payout={approving} title={stationName(approving.stationId) ?? t('settlements.openStation')} />
        </ConfirmSheet>
      )}
      <ReasonSheet
        open={Boolean(cancelling)}
        onOpenChange={() => setCancelling(undefined)}
        title={t('settlements.payouts.cancelTitle')}
        description={t('settlements.payouts.cancelHint')}
        submitLabel={t('settlements.payouts.cancel')}
        submitVariant="danger"
        onConfirm={async (reason) => {
          if (!cancelling) return;
          await opsApi.finance.cancelPayout(cancelling.id, reason);
          toast(t('settlements.payouts.cancelled'));
          payouts.reload();
        }}
      />
    </>
  );
}

type BankChangeFilter = 'ALL' | BankAccountStatus;

function BankChangeFacts({ change }: { change: BankAccount }) {
  const { t } = useTranslation('live');
  return (
    <>
      <span>{change.bankName}</span>
      <span>{change.holderName}</span>
      <span>
        {t('finance.accountEnding')} <span dir="ltr" className="font-mono">{change.accountLast4}</span>
      </span>
      <span>
        {t('finance.letter')} <span dir="ltr">{change.bankLetterReference}</span>
      </span>
      <DateTimeText value={change.requestedAt} />
    </>
  );
}

function BankChangesTab() {
  const { t } = useTranslation('live');
  const [status, setStatus] = useState<BankChangeFilter>('PENDING');
  const [approving, setApproving] = useState<BankAccount>();
  const [rejecting, setRejecting] = useState<BankAccount>();
  const stationName = useStationNames((signal) => opsApi.stations.list({ limit: 100 }, signal));
  const changes = usePagedResource(
    (page, signal) => opsApi.finance.bankChanges({ ...page, status: status === 'ALL' ? undefined : status }, signal),
    [status],
  );
  return (
    <>
      <SegmentedControl<BankChangeFilter>
        className="mb-4"
        label={t('settlements.statusFilter')}
        value={status}
        onValueChange={setStatus}
        options={[
          { value: 'PENDING', label: t('settlements.bankChanges.status.PENDING') },
          { value: 'ACTIVE', label: t('settlements.bankChanges.status.ACTIVE') },
          { value: 'REJECTED', label: t('settlements.bankChanges.status.REJECTED') },
          { value: 'ALL', label: t('common.all') },
        ]}
      />
      <ListPanel>
        <PagedList resource={changes} empty={<EmptyList title={t('settlements.bankChanges.empty')} description={t('settlements.bankChanges.emptyHint')} />}>
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((change) => (
                <FinanceRow
                  key={change.id}
                  title={<StationLink id={change.stationId} name={stationName(change.stationId)} />}
                  chip={<BankChangeStatusChip status={change.status} />}
                  facts={<BankChangeFacts change={change} />}
                  notes={
                    change.decisionReason && (
                      <p className="mt-1 text-sm text-muted">{t('settlements.reason', { reason: change.decisionReason })}</p>
                    )
                  }
                  actions={
                    change.status === 'PENDING' && (
                      <>
                        <Button size="sm" onClick={() => setApproving(change)}>
                          {t('settlements.bankChanges.approve')}
                        </Button>
                        <Button size="sm" variant="ghost" onClick={() => setRejecting(change)}>
                          {t('settlements.bankChanges.reject')}
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
      {approving && (
        <ConfirmSheet
          title={t('settlements.bankChanges.approveTitle')}
          description={t('settlements.bankChanges.approveHint')}
          submitLabel={t('settlements.bankChanges.approve')}
          onClose={() => setApproving(undefined)}
          onConfirm={async () => {
            await opsApi.finance.approveBankChange(approving.id);
            toast(t('settlements.bankChanges.approved'));
            changes.reload();
          }}
        >
          <FinanceRow
            title={stationName(approving.stationId) ?? t('settlements.openStation')}
            chip={<BankChangeStatusChip status={approving.status} />}
            facts={<BankChangeFacts change={approving} />}
          />
        </ConfirmSheet>
      )}
      <ReasonSheet
        open={Boolean(rejecting)}
        onOpenChange={() => setRejecting(undefined)}
        title={t('settlements.bankChanges.rejectTitle')}
        description={t('settlements.bankChanges.rejectHint')}
        submitLabel={t('settlements.bankChanges.reject')}
        submitVariant="danger"
        onConfirm={async (reason) => {
          if (!rejecting) return;
          await opsApi.finance.rejectBankChange(rejecting.id, reason);
          toast(t('settlements.bankChanges.rejected'));
          changes.reload();
        }}
      />
    </>
  );
}

type HoldFilter = 'ALL' | HoldState;

function HoldsTab() {
  const { t } = useTranslation('live');
  const [state, setState] = useState<HoldFilter>('HELD');
  const [releasing, setReleasing] = useState<HeldPayable>();
  const stationName = useStationNames((signal) => opsApi.stations.list({ limit: 100 }, signal));
  const holds = usePagedResource(
    (page, signal) => opsApi.finance.holds({ ...page, state: state === 'ALL' ? undefined : state }, signal),
    [state],
  );
  return (
    <>
      <SegmentedControl<HoldFilter>
        className="mb-4"
        label={t('settlements.statusFilter')}
        value={state}
        onValueChange={setState}
        options={[
          { value: 'HELD', label: t('settlements.holds.state.HELD') },
          { value: 'RELEASED', label: t('settlements.holds.state.RELEASED') },
          { value: 'REVERSED', label: t('settlements.holds.state.REVERSED') },
          { value: 'ALL', label: t('common.all') },
        ]}
      />
      <ListPanel>
        <PagedList resource={holds} empty={<EmptyList title={t('settlements.holds.empty')} description={t('settlements.holds.emptyHint')} />}>
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((hold) => (
                <FinanceRow
                  key={hold.fuelingId}
                  title={<StationLink id={hold.stationId} name={stationName(hold.stationId)} />}
                  amount={hold.amountPiastres}
                  chip={<HoldStateChip state={hold.state} />}
                  facts={
                    <>
                      <Link to={`/ops/transactions/${hold.fuelingId}`} dir="ltr" className="font-mono text-primary hover:underline">
                        {hold.reference}
                      </Link>
                      <Plate value={hold.plate} size="sm" />
                      <DateTimeText value={hold.evaluatedAt} />
                    </>
                  }
                  notes={
                    <>
                      {hold.conflicts.length > 0 && (
                        <p className="mt-1 text-sm">{(hold.conflicts as Fueling['conflicts']).map((conflict) => t(`fuelings.conflict.${conflict}`)).join(' · ')}</p>
                      )}
                      {hold.releaseReason && <p className="mt-1 text-sm text-muted">{t('settlements.reason', { reason: hold.releaseReason })}</p>}
                    </>
                  }
                  actions={
                    hold.state === 'HELD' && (
                      <Button size="sm" onClick={() => setReleasing(hold)}>
                        {t('settlements.holds.release')}
                      </Button>
                    )
                  }
                />
              ))}
            </div>
          )}
        </PagedList>
      </ListPanel>
      <ReasonSheet
        open={Boolean(releasing)}
        onOpenChange={() => setReleasing(undefined)}
        title={t('settlements.holds.releaseTitle')}
        description={t('settlements.holds.releaseHint')}
        submitLabel={t('settlements.holds.release')}
        onConfirm={async (reason) => {
          if (!releasing) return;
          await opsApi.finance.releaseHold(releasing.fuelingId, reason);
          toast(t('settlements.holds.released'));
          holds.reload();
        }}
      />
    </>
  );
}

type ReversalFilter = 'ALL' | ReversalStatus;

function ReversalsTab() {
  const { t } = useTranslation('live');
  const [status, setStatus] = useState<ReversalFilter>('PENDING');
  const [approving, setApproving] = useState<Reversal>();
  const [rejecting, setRejecting] = useState<Reversal>();
  const stationName = useStationNames((signal) => opsApi.stations.list({ limit: 100 }, signal));
  const reversals = usePagedResource(
    (page, signal) => opsApi.finance.reversals({ ...page, status: status === 'ALL' ? undefined : status }, signal),
    [status],
  );
  const title = (reversal: Reversal) => (
    <Link to={`/ops/transactions/${reversal.fuelingId}`} className="text-primary hover:underline">
      {stationName(reversal.stationId) ?? t('settlements.reversals.openFueling')}
    </Link>
  );
  return (
    <>
      <SegmentedControl<ReversalFilter>
        className="mb-4"
        label={t('settlements.statusFilter')}
        value={status}
        onValueChange={setStatus}
        options={[
          { value: 'PENDING', label: t('settlements.reversals.status.PENDING') },
          { value: 'APPROVED', label: t('settlements.reversals.status.APPROVED') },
          { value: 'REJECTED', label: t('settlements.reversals.status.REJECTED') },
          { value: 'ALL', label: t('common.all') },
        ]}
      />
      <ListPanel>
        <PagedList resource={reversals} empty={<EmptyList title={t('settlements.reversals.empty')} description={t('settlements.reversals.emptyHint')} />}>
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((reversal) => (
                <ReversalRow
                  key={reversal.id}
                  reversal={reversal}
                  title={title(reversal)}
                  actions={
                    reversal.status === 'PENDING' && (
                      <>
                        <Button size="sm" onClick={() => setApproving(reversal)}>
                          {t('settlements.reversals.approve')}
                        </Button>
                        <Button size="sm" variant="ghost" onClick={() => setRejecting(reversal)}>
                          {t('settlements.reversals.reject')}
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
      {approving && (
        <ConfirmSheet
          title={t('settlements.reversals.approveTitle')}
          description={t('settlements.reversals.approveHint')}
          submitLabel={t('settlements.reversals.approve')}
          onClose={() => setApproving(undefined)}
          onConfirm={async () => {
            await opsApi.finance.approveReversal(approving.id);
            toast(t('settlements.reversals.approved'));
            reversals.reload();
          }}
        >
          <ReversalRow reversal={approving} title={title(approving)} />
        </ConfirmSheet>
      )}
      <ReasonSheet
        open={Boolean(rejecting)}
        onOpenChange={() => setRejecting(undefined)}
        title={t('settlements.reversals.rejectTitle')}
        description={t('settlements.reversals.rejectHint')}
        submitLabel={t('settlements.reversals.reject')}
        submitVariant="danger"
        onConfirm={async (reason) => {
          if (!rejecting) return;
          await opsApi.finance.rejectReversal(rejecting.id, reason);
          toast(t('settlements.reversals.rejected'));
          reversals.reload();
        }}
      />
    </>
  );
}

/**
 * Ops: money going out to stations and refunds going back to companies. Every payout, bank-account
 * change and reversal waits for a second person (the API refuses the same user approving their own
 * request); held payables are released here once Operations has looked at an offline fueling.
 */
export function SettlementsPage() {
  const { t } = useTranslation('live');
  return (
    <>
      <PageHeader title={t('settlements.title')} description={t('settlements.description')} />
      <Tabs
        label={t('settlements.tabsLabel')}
        items={[
          { value: 'payouts', label: t('settlements.tabs.payouts'), content: <PayoutsTab /> },
          { value: 'bankChanges', label: t('settlements.tabs.bankChanges'), content: <BankChangesTab /> },
          { value: 'holds', label: t('settlements.tabs.holds'), content: <HoldsTab /> },
          { value: 'reversals', label: t('settlements.tabs.reversals'), content: <ReversalsTab /> },
        ]}
      />
    </>
  );
}
