/**
 * Wallet building blocks shared by the company portal and the Ops console: the balance, the
 * statement, a top-up row and the bank-transfer form. Amounts are integer piastres throughout.
 */
import { ArrowDownLeft, ArrowUpRight, CalendarDays, Wallet as WalletIcon } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Banner, DateTimeText, Icon, Money, SelectField, Skeleton, TextField, toast } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatDate, formatMoney } from '@/lib/format';
import { cn } from '@/lib/cn';
import type { StatementEntry, TopUp, TopUpRequestBody, Wallet } from './api';
import { TopUpStatusChip } from './chips';
import { EmptyList, FormSheet, PagedList } from './components';
import { optional, parsePounds } from './input';
import type { PagedResource } from './use-resource';

/** The balance, with the low-balance warning when it applies. */
export function BalanceCard({ wallet, loading, action }: { wallet?: Wallet; loading: boolean; action?: ReactNode }) {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  if (!wallet) {
    return loading ? <Skeleton className="h-36" /> : null;
  }
  return (
    <div className="flex flex-col gap-4">
      {/* Wraps instead of a fixed row: in a narrow column the action drops under the balance. */}
      <div className="flex flex-wrap items-end justify-between gap-4 rounded-container bg-hero p-5 text-sidebar-active-foreground">
        <div className="flex min-w-0 items-start gap-3">
          <span className="mt-1 flex size-10 items-center justify-center rounded-full bg-sidebar-active">
            <Icon icon={WalletIcon} />
          </span>
          <div className="min-w-0">
            <p className="text-sm text-sidebar-muted">{t('wallet.balance')}</p>
            <p className="text-3xl font-semibold tabular-nums">
              <Money value={wallet.balancePiastres} />
            </p>
            <p className="mt-1 text-xs text-sidebar-muted">
              {t('wallet.threshold')} {formatMoney(wallet.lowBalanceThresholdPiastres, locale, { precision: 'whole' })}
            </p>
          </div>
        </div>
        {action}
      </div>
      {wallet.lowBalance && (
        <Banner
          tone="warning"
          title={t('wallet.lowBalance')}
          description={t('wallet.lowBalanceHint', { threshold: formatMoney(wallet.lowBalanceThresholdPiastres, locale, { precision: 'whole' }) })}
        />
      )}
    </div>
  );
}

/** Wallet movements, newest first, each with the balance right after it. */
export function StatementList({ resource }: { resource: PagedResource<StatementEntry> }) {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  return (
    <PagedList resource={resource} empty={<EmptyList title={t('wallet.noEntries')} description={t('wallet.noEntriesHint')} />}>
      {(items) => (
        <ul className="divide-y divide-border">
          {items.map((entry) => {
            const credit = entry.direction === 'CREDIT';
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
                  <p className="font-medium">{t(`wallet.sources.${entry.sourceType}`)}</p>
                  <p className="truncate text-xs text-muted">
                    <DateTimeText value={entry.postedAt} />
                    {entry.reference && (
                      <>
                        {' · '}
                        <span dir="ltr">{entry.reference}</span>
                      </>
                    )}
                  </p>
                </div>
                <div className="text-end">
                  <p className={cn('font-semibold tabular-nums', credit && 'text-success')}>
                    <Money value={credit ? entry.amountPiastres : -entry.amountPiastres} signDisplay="always" />
                  </p>
                  <p className="text-xs text-muted tabular-nums">
                    {t('wallet.balanceAfter', { amount: formatMoney(entry.balanceAfterPiastres, locale) })}
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
 * One top-up: the amount and what identifies the transfer at the start, the status and the
 * decision buttons together at the end, so every row puts its actions in the same place.
 */
export function TopUpRow({ topUp, title, actions }: { topUp: TopUp; title?: ReactNode; actions?: ReactNode }) {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const verified = topUp.verifiedAmountPiastres !== undefined && topUp.verifiedAmountPiastres !== null;
  return (
    <div className="flex flex-col gap-3 px-4 py-4 md:flex-row md:items-center md:gap-6 md:px-5">
      <div className="min-w-0 flex-1">
        {title && <p className="text-sm font-medium">{title}</p>}
        <p className="text-xl font-semibold tracking-tight tabular-nums">
          <Money value={topUp.amountPiastres} />
        </p>
        <p className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
          <span dir="ltr" className="font-medium text-foreground">
            {topUp.bankReference}
          </span>
          <span className="flex items-center gap-1">
            <Icon icon={CalendarDays} size={13} />
            {t('topUps.transferredOn', { date: formatDate(topUp.transferDate, locale, { withYear: true }) })}
          </span>
          <span>{t(`topUps.origin.${topUp.origin}`)}</span>
          {topUp.receivingAccount && (
            <span>
              {t('topUps.receivingAccount')} <span dir="ltr">{topUp.receivingAccount}</span>
            </span>
          )}
        </p>
        {(verified || topUp.verifiedBy || topUp.evidenceReference) && (
          <p className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
            {verified && (
              <span>
                {t('topUps.verifiedAmount')}{' '}
                <span className="font-semibold text-foreground tabular-nums">
                  <Money value={topUp.verifiedAmountPiastres!} />
                </span>
              </span>
            )}
            {topUp.verifiedBy && (
              <span>
                {t('topUps.verifiedBy')}{' '}
                <span dir="ltr" className="font-mono">
                  {topUp.verifiedBy.slice(0, 8)}
                </span>
              </span>
            )}
            {topUp.evidenceReference && (
              <span>
                {t('topUps.evidence')} <span dir="ltr">{topUp.evidenceReference}</span>
              </span>
            )}
          </p>
        )}
        {topUp.note && <p className="mt-2 text-sm text-pretty">{topUp.note}</p>}
        {topUp.decisionReason && (
          <p className="mt-2 text-sm text-pretty text-muted">
            {t('topUps.reason', { reason: '' })}
            <bdi>{topUp.decisionReason}</bdi>
          </p>
        )}
      </div>
      <div className="flex shrink-0 flex-wrap items-center justify-between gap-3 md:flex-col md:items-end md:justify-center">
        <TopUpStatusChip status={topUp.status} />
        {actions && <div className="flex flex-wrap items-center gap-2 md:justify-end">{actions}</div>}
      </div>
    </div>
  );
}

/** Today in Cairo as yyyy-mm-dd: a transfer cannot be dated later. */
function todayInCairo(): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Africa/Cairo' }).format(new Date());
}

/**
 * A bank transfer: the company reports it (a request Ops checks), or Ops credits it directly. The
 * same reference can never be credited twice; the API enforces that and the form says why.
 */
export function TransferSheet({
  title,
  description,
  submitLabel,
  doneMessage,
  send,
  accounts,
  onClose,
  onDone,
}: {
  title: string;
  description: string;
  submitLabel: string;
  doneMessage: string;
  send: (body: TopUpRequestBody & { receivingAccount?: string; evidenceReference?: string }) => Promise<unknown>;
  /** Ops only: the ZAWED accounts a transfer can arrive in. Adds the receiving account and evidence fields. */
  accounts?: string[];
  onClose: () => void;
  onDone: () => void;
}) {
  const { t } = useTranslation('live');
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');
  const [date, setDate] = useState(todayInCairo());
  const [note, setNote] = useState('');
  const [account, setAccount] = useState(accounts?.[0] ?? '');
  const [evidence, setEvidence] = useState('');
  const [errors, setErrors] = useState<{ amount?: string; reference?: string; date?: string }>({});
  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={title}
      description={description}
      submitLabel={submitLabel}
      onSubmit={async () => {
        const piastres = parsePounds(amount);
        const found = {
          amount: piastres === null || piastres <= 0 ? t('errors.invalidAmount') : undefined,
          reference: reference.trim() ? undefined : t('errors.required'),
          date: !date ? t('errors.required') : date > todayInCairo() ? t('errors.TRANSFER_DATE_IN_FUTURE') : undefined,
        };
        if (found.amount || found.reference || found.date) {
          setErrors(found);
          return false;
        }
        await send({
          amountPiastres: piastres ?? 0,
          bankReference: reference.trim(),
          transferDate: date,
          note: optional(note),
          ...(accounts ? { receivingAccount: account || undefined, evidenceReference: optional(evidence) } : {}),
        });
        toast(doneMessage);
        onDone();
      }}
      onError={(failure) => {
        if (failure.field === 'bankReference') setErrors({ reference: failure.message });
        if (failure.field === 'transferDate') setErrors({ date: failure.message });
      }}
    >
      <TextField
        label={t('topUps.amount')}
        inputMode="decimal"
        dir="ltr"
        value={amount}
        error={errors.amount}
        onChange={(event) => setAmount(event.target.value)}
      />
      <TextField
        label={t('topUps.bankReference')}
        hint={t('topUps.bankReferenceHint')}
        dir="ltr"
        maxLength={64}
        autoComplete="off"
        spellCheck={false}
        value={reference}
        error={errors.reference}
        onChange={(event) => setReference(event.target.value)}
      />
      <TextField
        label={t('topUps.transferDate')}
        type="date"
        dir="ltr"
        max={todayInCairo()}
        value={date}
        error={errors.date}
        onChange={(event) => setDate(event.target.value)}
      />
      {accounts && (
        <>
          <SelectField
            label={t('topUps.receivingAccount')}
            value={account}
            onValueChange={setAccount}
            options={accounts.map((value) => ({ value, label: value }))}
          />
          <TextField
            label={t('common.optional', { label: t('topUps.evidenceReference') })}
            hint={t('topUps.evidenceReferenceHint')}
            maxLength={200}
            value={evidence}
            onChange={(event) => setEvidence(event.target.value)}
          />
        </>
      )}
      <TextField label={t('common.optional', { label: t('topUps.note') })} maxLength={500} value={note} onChange={(event) => setNote(event.target.value)} />
    </FormSheet>
  );
}
