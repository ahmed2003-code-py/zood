/**
 * Wallet building blocks shared by the company portal and the Ops console: the balance, the
 * statement, a top-up row and the bank-transfer form. Amounts are integer piastres throughout.
 */
import { ArrowDownLeft, ArrowUpRight, CalendarDays, Wallet as WalletIcon } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Banner, DateTimeText, Icon, Money, Skeleton, TextField, toast } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatMoney } from '@/lib/format';
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

/** One top-up: amount, reference, transfer date, status and, when decided, the reason. */
export function TopUpRow({ topUp, title, actions }: { topUp: TopUp; title?: ReactNode; actions?: ReactNode }) {
  const { t } = useTranslation('live');
  return (
    <div className="flex flex-col gap-2 px-4 py-3">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          {title && <p className="text-sm text-muted">{title}</p>}
          <p className="text-lg font-semibold tabular-nums">
            <Money value={topUp.amountPiastres} />
          </p>
          <p className="flex flex-wrap items-center gap-x-2 text-xs text-muted">
            <span dir="ltr" className="font-mono">
              {topUp.bankReference}
            </span>
            <span className="flex items-center gap-1">
              <Icon icon={CalendarDays} size={12} />
              {t('topUps.transferredOn', { date: topUp.transferDate })}
            </span>
            {!topUp.requestedBy && <span>{t('topUps.directCredit')}</span>}
          </p>
          {topUp.note && <p className="mt-1 text-sm">{topUp.note}</p>}
          {topUp.decisionReason && <p className="mt-1 text-sm text-muted">{t('topUps.reason', { reason: topUp.decisionReason })}</p>}
        </div>
        <TopUpStatusChip status={topUp.status} />
      </div>
      {actions && <div className="flex flex-wrap gap-2">{actions}</div>}
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
  onClose,
  onDone,
}: {
  title: string;
  description: string;
  submitLabel: string;
  doneMessage: string;
  send: (body: TopUpRequestBody) => Promise<unknown>;
  onClose: () => void;
  onDone: () => void;
}) {
  const { t } = useTranslation('live');
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');
  const [date, setDate] = useState(todayInCairo());
  const [note, setNote] = useState('');
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
        await send({ amountPiastres: piastres ?? 0, bankReference: reference.trim(), transferDate: date, note: optional(note) });
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
      <TextField label={t('common.optional', { label: t('topUps.note') })} maxLength={500} value={note} onChange={(event) => setNote(event.target.value)} />
    </FormSheet>
  );
}
