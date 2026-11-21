import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router';
import { useSession } from '@/auth/session-store';
import { Banner, Button, DateTimeText, Money, SegmentedControl, SelectField, Tabs, TextField, toast } from '@/design-system';
import { cn } from '@/lib/cn';
import { useLocale } from '@/i18n/useLocale';
import { ListPanel, PageHeader } from '@/shell/page';
import { opsApi, type AdjustmentStatus, type TopUp, type TopUpStatus, type WalletAdjustment } from '../api';
import { AdjustmentStatusChip } from '../chips';
import { EmptyList, FormSheet, PagedList, ReasonSheet } from '../components';
import { optional, parsePounds } from '../input';
import { useAccountLabel, useActorName, useIsOwnStep } from '../actors';
import { usePagedResource, useResource } from '../use-resource';
import { TopUpRow } from '../wallet';

type Filter = 'ALL' | TopUpStatus;
type AdjustmentFilter = 'ALL' | AdjustmentStatus;

/** Platform roles that may approve money (permission TOPUP_APPROVE). The API enforces it; this only hides buttons. */
const APPROVER_ROLES = ['OPS_MANAGER', 'SUPER_ADMIN'];

/** Names for the companies on screen: the first 100 cover a pilot; beyond that the id is shown. */
function useCompanyName() {
  const { locale } = useLocale();
  const companies = useResource((signal) => opsApi.companies.list({ limit: 100 }, signal), []);
  return (id: string) => {
    const company = companies.data?.items.find((item) => item.id === id);
    return company ? (locale === 'ar' ? company.nameAr : company.nameEn) : id;
  };
}

function useApprover() {
  const user = useSession((state) => state.user);
  return { userId: user?.id, canApprove: Boolean(user && APPROVER_ROLES.includes(user.role)) };
}

const TOP_UP_FILTERS: readonly Filter[] = ['PENDING', 'PENDING_APPROVAL', 'CONFIRMED', 'REJECTED', 'ALL'];
const ADJUSTMENT_FILTERS: readonly AdjustmentFilter[] = ['PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'CANCELLED', 'ALL'];

/** A status from the address (?status=), when it is one this list knows. */
function useStatusParam<T extends string>(allowed: readonly T[]): T | undefined {
  const [params] = useSearchParams();
  const value = params.get('status');
  return allowed.find((item) => item === value);
}

/** The company's figure and the verified figure side by side; a difference is flagged. */
function AmountComparison({ claimed, verified }: { claimed: number; verified: number }) {
  const { t } = useTranslation('live');
  const differs = claimed !== verified;
  return (
    <div className="flex flex-col gap-3">
      <div className="grid grid-cols-2 gap-3">
        <div className="rounded-control border border-border bg-surface-sunken p-3">
          <p className="text-xs text-muted">{t('topUps.claimedAmount')}</p>
          <p className="text-xl font-semibold tabular-nums">
            <Money value={claimed} />
          </p>
        </div>
        <div className={cn('rounded-control border p-3', differs ? 'border-warning bg-warning-subtle' : 'border-border bg-surface-sunken')}>
          <p className="text-xs text-muted">{t('topUps.verifiedAmount')}</p>
          <p className="text-xl font-semibold tabular-nums">
            <Money value={verified} />
          </p>
        </div>
      </div>
      {differs && <Banner tone="warning" title={t('topUps.amountsDiffer')} description={t('topUps.amountsDifferHint')} />}
    </div>
  );
}

/**
 * Ops: bank transfers reported by companies, in two steps by two people. Anyone in Operations
 * verifies a transfer against the bank statement and records what actually arrived; an Operations
 * Manager or Super Admin, never the same person, approves it and only then is the wallet credited.
 */
function TopUpsTab() {
  const { t } = useTranslation('live');
  const { userId, canApprove } = useApprover();
  const accountLabel = useAccountLabel();
  const fromAddress = useStatusParam(TOP_UP_FILTERS);
  // An Ops Manager's own queue is what waits for approval (QA round 1); Ops starts with what waits for verification.
  const [status, setStatus] = useState<Filter>(fromAddress ?? (canApprove ? 'PENDING_APPROVAL' : 'PENDING'));
  const [verifying, setVerifying] = useState<TopUp>();
  const [approving, setApproving] = useState<TopUp>();
  const [rejecting, setRejecting] = useState<TopUp>();
  const topUps = usePagedResource(
    (page, signal) => opsApi.topUps.list({ ...page, status: status === 'ALL' ? undefined : status }, signal),
    [status],
  );
  const accounts = useResource((signal) => opsApi.topUps.receivingAccounts(signal), []);
  const companyName = useCompanyName();

  const actionsFor = (topUp: TopUp): ReactNode => {
    if (topUp.status === 'PENDING') {
      return (
        <>
          <Button size="sm" onClick={() => setVerifying(topUp)}>
            {t('topUps.verify')}
          </Button>
          <Button size="sm" variant="ghost" onClick={() => setRejecting(topUp)}>
            {t('topUps.reject')}
          </Button>
        </>
      );
    }
    if (topUp.status !== 'PENDING_APPROVAL') return null;
    const own = Boolean(userId) && topUp.verifiedBy === userId;
    return (
      <>
        {canApprove ? (
          <Button size="sm" disabled={own} onClick={() => setApproving(topUp)}>
            {t('topUps.approve')}
          </Button>
        ) : null}
        <Button size="sm" variant="ghost" onClick={() => setRejecting(topUp)}>
          {t('topUps.reject')}
        </Button>
        <p className="w-full text-xs text-muted">{t(!canApprove ? 'topUps.needsManager' : own ? 'topUps.ownVerification' : 'topUps.awaitingApproval')}</p>
      </>
    );
  };

  return (
    <>
      <SegmentedControl<Filter>
        className="mb-4"
        label={t('topUps.statusFilter')}
        value={status}
        onValueChange={setStatus}
        options={[
          { value: 'PENDING', label: t('topUps.status.PENDING') },
          { value: 'PENDING_APPROVAL', label: t('topUps.status.PENDING_APPROVAL') },
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
                  actions={actionsFor(topUp)}
                />
              ))}
            </div>
          )}
        </PagedList>
      </ListPanel>

      {verifying && (
        <VerifySheet
          topUp={verifying}
          company={companyName(verifying.companyId)}
          accounts={accounts.data ?? []}
          onClose={() => setVerifying(undefined)}
          onDone={topUps.reload}
        />
      )}
      {approving && (
        <FormSheet
          open
          onOpenChange={() => setApproving(undefined)}
          title={t('topUps.approveTitle', { company: companyName(approving.companyId) })}
          description={t('topUps.approveHint')}
          submitLabel={t('topUps.approve')}
          onSubmit={async () => {
            await opsApi.topUps.approve(approving.id);
            toast(t('topUps.approved'));
            topUps.reload();
          }}
        >
          <AmountComparison claimed={approving.amountPiastres} verified={approving.verifiedAmountPiastres ?? approving.amountPiastres} />
          <dl className="grid grid-cols-2 gap-x-3 gap-y-1 text-sm">
            <dt className="text-muted">{t('topUps.receivingAccount')}</dt>
            <dd className="text-start">
              <bdi>{accountLabel(approving.receivingAccount, approving.receivingAccountLabel)}</bdi>
            </dd>
            <dt className="text-muted">{t('topUps.bankReference')}</dt>
            <dd dir="ltr" className="text-start font-mono">
              {approving.bankReference}
            </dd>
            {approving.evidenceReference && (
              <>
                <dt className="text-muted">{t('topUps.evidenceReference')}</dt>
                <dd dir="ltr" className="text-start">
                  {approving.evidenceReference}
                </dd>
              </>
            )}
          </dl>
        </FormSheet>
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

/**
 * Verifying records what actually arrived (not what the company said) and where it arrived. It
 * credits nothing: a second person approves it next.
 */
function VerifySheet({
  topUp,
  company,
  accounts,
  onClose,
  onDone,
}: {
  topUp: TopUp;
  company: string;
  accounts: string[];
  onClose: () => void;
  onDone: () => void;
}) {
  const { t } = useTranslation('live');
  const accountLabel = useAccountLabel();
  const accountOptions = accounts.length > 0 ? accounts : [topUp.receivingAccount];
  const [amount, setAmount] = useState('');
  const [account, setAccount] = useState(accountOptions.includes(topUp.receivingAccount) ? topUp.receivingAccount : (accountOptions[0] ?? ''));
  const [reference, setReference] = useState(topUp.bankReference);
  const [date, setDate] = useState(topUp.transferDate);
  const [evidence, setEvidence] = useState('');
  const [note, setNote] = useState('');
  const [errors, setErrors] = useState<{ amount?: string; reference?: string; date?: string }>({});
  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={t('topUps.verifyTitle', { company })}
      description={t('topUps.verifyHint')}
      submitLabel={t('topUps.verify')}
      onSubmit={async () => {
        const piastres = parsePounds(amount);
        if (piastres === null || piastres <= 0) {
          setErrors({ amount: t('errors.invalidAmount') });
          return false;
        }
        await opsApi.topUps.verify(topUp.id, {
          receivedAmountPiastres: piastres,
          receivingAccount: account || undefined,
          bankReference: reference.trim() && reference.trim() !== topUp.bankReference ? reference.trim() : undefined,
          transferDate: date && date !== topUp.transferDate ? date : undefined,
          evidenceReference: optional(evidence),
          note: optional(note),
        });
        toast(t('topUps.verified'));
        onDone();
      }}
      onError={(failure) => {
        if (failure.field === 'bankReference') setErrors({ reference: failure.message });
        if (failure.field === 'transferDate') setErrors({ date: failure.message });
        if (failure.field === 'receivedAmountPiastres') setErrors({ amount: failure.message });
      }}
    >
      <div className="rounded-control border border-border bg-surface-sunken p-3">
        <p className="text-xs text-muted">{t('topUps.claimedAmount')}</p>
        <p className="text-xl font-semibold tabular-nums">
          <Money value={topUp.amountPiastres} />
        </p>
        <p dir="ltr" className="font-mono text-sm text-muted">
          {topUp.bankReference}
        </p>
      </div>
      <TextField
        label={t('topUps.receivedAmount')}
        hint={t('topUps.receivedAmountHint')}
        inputMode="decimal"
        dir="ltr"
        value={amount}
        error={errors.amount}
        onChange={(event) => {
          setAmount(event.target.value);
          setErrors({});
        }}
      />
      <SelectField
        label={t('topUps.receivingAccount')}
        value={account}
        onValueChange={setAccount}
        options={accountOptions.map((value) => ({ value, label: accountLabel(value) }))}
      />
      <TextField
        label={t('common.optional', { label: t('topUps.correctedReference') })}
        hint={t('topUps.correctedReferenceHint')}
        dir="ltr"
        maxLength={64}
        autoComplete="off"
        spellCheck={false}
        value={reference}
        error={errors.reference}
        onChange={(event) => {
          setReference(event.target.value);
          setErrors({});
        }}
      />
      <TextField
        label={t('common.optional', { label: t('topUps.transferDate') })}
        type="date"
        dir="ltr"
        value={date}
        error={errors.date}
        onChange={(event) => {
          setDate(event.target.value);
          setErrors({});
        }}
      />
      <TextField
        label={t('topUps.evidenceReference')}
        hint={t('topUps.evidenceReferenceHint')}
        maxLength={200}
        value={evidence}
        onChange={(event) => setEvidence(event.target.value)}
      />
      <TextField label={t('common.optional', { label: t('topUps.note') })} maxLength={500} value={note} onChange={(event) => setNote(event.target.value)} />
    </FormSheet>
  );
}

/**
 * Wallet adjustments: a credit with no bank transfer behind it, requested from the company page
 * and decided by someone else. The requester can withdraw it; an approver cannot approve their own.
 */
function AdjustmentsTab() {
  const { t } = useTranslation(['live', 'portal']);
  const { canApprove } = useApprover();
  const actorName = useActorName();
  const isOwn = useIsOwnStep();
  const fromAddress = useStatusParam(ADJUSTMENT_FILTERS);
  const [status, setStatus] = useState<AdjustmentFilter>(fromAddress ?? 'PENDING_APPROVAL');
  const [approving, setApproving] = useState<WalletAdjustment>();
  const [rejecting, setRejecting] = useState<WalletAdjustment>();
  const [cancelling, setCancelling] = useState<WalletAdjustment>();
  const adjustments = usePagedResource(
    (page, signal) => opsApi.adjustments.list({ ...page, status: status === 'ALL' ? undefined : status }, signal),
    [status],
  );
  const companyName = useCompanyName();

  const actionsFor = (item: WalletAdjustment): ReactNode => {
    if (item.status !== 'PENDING_APPROVAL') return null;
    const own = isOwn(item, 'requestedBy');
    return (
      <>
        {canApprove && !own && (
          <>
            <Button size="sm" onClick={() => setApproving(item)}>
              {t('adjustments.approve')}
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setRejecting(item)}>
              {t('adjustments.reject')}
            </Button>
          </>
        )}
        {own && (
          <Button size="sm" variant="ghost" onClick={() => setCancelling(item)}>
            {t('adjustments.cancel')}
          </Button>
        )}
        <p className="w-full text-xs text-muted">{t(own ? 'adjustments.ownRequest' : canApprove ? 'adjustments.awaitingYou' : 'adjustments.needsManager')}</p>
      </>
    );
  };

  return (
    <>
      <SegmentedControl<AdjustmentFilter>
        className="mb-4"
        label={t('topUps.statusFilter')}
        value={status}
        onValueChange={setStatus}
        options={[
          { value: 'PENDING_APPROVAL', label: t('adjustments.status.PENDING_APPROVAL') },
          { value: 'APPROVED', label: t('adjustments.status.APPROVED') },
          { value: 'REJECTED', label: t('adjustments.status.REJECTED') },
          { value: 'CANCELLED', label: t('adjustments.status.CANCELLED') },
          { value: 'ALL', label: t('common.all') },
        ]}
      />
      <ListPanel>
        <PagedList resource={adjustments} empty={<EmptyList title={t('adjustments.empty')} description={t('adjustments.emptyHint')} />}>
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((item) => (
                <div key={item.id} className="flex flex-col gap-2 px-4 py-3">
                  <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                      <p className="text-sm text-muted">
                        <Link to={`/ops/companies/${item.companyId}`} className="font-medium text-foreground hover:underline">
                          {companyName(item.companyId)}
                        </Link>
                      </p>
                      <p className="text-lg font-semibold tabular-nums">
                        <Money value={item.amountPiastres} />
                      </p>
                      <p className="mt-1 text-sm break-words">{item.reason}</p>
                      <p className="text-xs text-muted">
                        <DateTimeText value={item.requestedAt} />
                        {actorName(item, 'requestedBy') && <>{' · '}{t('portal:actor.requestedBy', { name: actorName(item, 'requestedBy') })}</>}
                        {item.status !== 'PENDING_APPROVAL' && actorName(item, 'decidedBy') && (
                          <>{' · '}{t('portal:actor.decidedBy', { name: actorName(item, 'decidedBy') })}</>
                        )}
                      </p>
                      {item.decisionReason && <p className="mt-1 text-sm break-words text-muted">{t('topUps.reason', { reason: item.decisionReason })}</p>}
                    </div>
                    <AdjustmentStatusChip status={item.status} />
                  </div>
                  {actionsFor(item) && <div className="flex flex-wrap gap-2">{actionsFor(item)}</div>}
                </div>
              ))}
            </div>
          )}
        </PagedList>
      </ListPanel>

      {approving && (
        <FormSheet
          open
          onOpenChange={() => setApproving(undefined)}
          title={t('adjustments.approveTitle', { company: companyName(approving.companyId) })}
          description={t('adjustments.approveHint')}
          submitLabel={t('adjustments.approve')}
          onSubmit={async () => {
            await opsApi.adjustments.approve(approving.id);
            toast(t('adjustments.approved'));
            adjustments.reload();
          }}
        >
          <div className="rounded-control border border-border bg-surface-sunken p-3">
            <p className="text-2xl font-semibold tabular-nums">
              <Money value={approving.amountPiastres} />
            </p>
            <p className="text-sm">{approving.reason}</p>
          </div>
        </FormSheet>
      )}
      {cancelling && (
        <FormSheet
          open
          onOpenChange={() => setCancelling(undefined)}
          title={t('adjustments.cancelTitle')}
          description={t('adjustments.cancelHint')}
          submitLabel={t('adjustments.cancel')}
          submitVariant="danger"
          onSubmit={async () => {
            await opsApi.adjustments.cancel(cancelling.id);
            toast(t('adjustments.cancelled'));
            adjustments.reload();
          }}
        >
          <div className="rounded-control border border-border bg-surface-sunken p-3">
            <p className="text-xl font-semibold tabular-nums">
              <Money value={cancelling.amountPiastres} />
            </p>
            <p className="text-sm">{cancelling.reason}</p>
          </div>
        </FormSheet>
      )}
      <ReasonSheet
        open={Boolean(rejecting)}
        onOpenChange={() => setRejecting(undefined)}
        title={t('adjustments.rejectTitle')}
        description={t('adjustments.rejectHint')}
        submitLabel={t('adjustments.reject')}
        submitVariant="danger"
        onConfirm={async (reason) => {
          if (!rejecting) return;
          await opsApi.adjustments.reject(rejecting.id, reason);
          toast(t('adjustments.rejected'));
          adjustments.reload();
        }}
      />
    </>
  );
}

export function TopUpsPage() {
  const { t } = useTranslation('live');
  const [params] = useSearchParams();
  return (
    <>
      <PageHeader title={t('topUps.title')} description={t('topUps.description')} />
      <Tabs
        label={t('topUps.tabsLabel')}
        defaultValue={params.get('tab') === 'adjustments' ? 'adjustments' : 'topups'}
        items={[
          { value: 'topups', label: t('topUps.tabs.topups'), content: <TopUpsTab /> },
          { value: 'adjustments', label: t('topUps.tabs.adjustments'), content: <AdjustmentsTab /> },
        ]}
      />
    </>
  );
}
