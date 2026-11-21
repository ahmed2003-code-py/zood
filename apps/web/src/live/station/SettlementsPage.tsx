/**
 * Station portal, finance: what ZAWED owes each station and what is available for the next payout,
 * the verified bank account (last four characters only) with a way to ask for a change, the payouts
 * received, the statement for a chosen period with its opening and closing balance and a CSV export,
 * and approved reversals. Read only: payouts are recorded by ZAWED Operations.
 *
 * No station endpoint changes a bank account yet, so "request a change" opens a ticket to
 * Operations, prefilled, that never carries an account number (Operations asks for the bank letter).
 */
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { ArrowDownLeft, ArrowUpRight, Download, Landmark, PencilLine } from 'lucide-react';
import { Banner, Button, EmptyState, ErrorState, Icon, Money, Plate, SelectField, Skeleton, StatusChip, TextAreaField, TextField, toast } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatMoney } from '@/lib/format';
import { cairoDay, periodRange } from '@/lib/periods';
import { ListPanel, PageHeader, PageSection } from '@/shell/page';
import { stationApi, type StationBankDetails, type StationStatementEntry } from '../api';
import { PayoutStatusChip, ReversalStatusChip } from '../chips';
import { FormSheet, PagedList } from '../components';
import { PayableCards } from '../finance';
import { cairoDayStart } from '../input';
import { usePagedResource, useResource, type Resource } from '../use-resource';
import { useValidRange, type DayRangeValue } from './hooks';
import { DayRangeFields, DayText, Stamp } from './parts';
import { statementPage, statementSummary } from './station-api';
import { csvCell, piastresToCsv } from './station-model';

/** The longest statement export, in pages of 100 lines. */
const EXPORT_PAGES = 50;

function StationFinance({ stationId, stationName }: { stationId: string; stationName: string }) {
  const { t } = useTranslation(['station', 'live']);
  const payable = useResource((signal) => stationApi.payable(stationId, signal), [stationId]);
  const bank = useResource((signal) => stationApi.bankAccount(stationId, signal), [stationId]);
  const payouts = usePagedResource((page, signal) => stationApi.payouts({ ...page, stationId }, signal), [stationId], 10);
  const reversals = usePagedResource((page, signal) => stationApi.reversals({ ...page, stationId }, signal), [stationId], 10);
  const [changing, setChanging] = useState(false);

  return (
    <div className="flex flex-col gap-8">
      <PayableCards resource={payable} />
      <div className="grid grid-cols-1 gap-8 lg:grid-cols-5">
        <div className="flex min-w-0 flex-col gap-8 lg:col-span-2">
          <PageSection title={t('live.finance.nextPayout')} description={t('live.finance.nextPayoutHint')}>
            <NextPayout payable={payable} bank={bank} lastPayout={payouts.items[0]} />
          </PageSection>
          <PageSection
            title={t('live:finance.bank')}
            action={
              <Button variant="secondary" size="sm" leadingIcon={PencilLine} onClick={() => setChanging(true)}>
                {t('live.finance.requestChange')}
              </Button>
            }
          >
            <BankAccount resource={bank} />
          </PageSection>
          <PageSection title={t('live.finance.payouts')}>
            <ListPanel>
              <PagedList resource={payouts} empty={<EmptyState icon={Landmark} title={t('live:stationSettlements.noPayouts')} description={t('live:stationSettlements.noPayoutsHint')} />}>
                {(items) => (
                  <ul className="divide-y divide-border">
                    {items.map((payout) => (
                      <li key={payout.id} className="flex items-start justify-between gap-3 px-4 py-3 md:px-5">
                        <div className="min-w-0">
                          <p className="text-lg font-semibold tabular-nums">
                            <Money value={payout.amountPiastres} />
                          </p>
                          <p className="text-xs text-muted">
                            {t('live.finance.valueDate')} <DayText day={payout.valueDate} /> · <bdi dir="ltr" className="font-mono">{payout.bankReference}</bdi>
                          </p>
                          {payout.note && <p className="mt-1 text-sm">{payout.note}</p>}
                        </div>
                        <PayoutStatusChip status={payout.status} />
                      </li>
                    ))}
                  </ul>
                )}
              </PagedList>
            </ListPanel>
          </PageSection>
        </div>
        <div className="flex min-w-0 flex-col gap-8 lg:col-span-3">
          <Statement stationId={stationId} stationName={stationName} />
          <PageSection title={t('live:stationSettlements.reversals')} description={t('live:stationSettlements.reversalsHint')}>
            <ListPanel>
              <PagedList resource={reversals} empty={<EmptyState title={t('live:stationSettlements.noReversals')} description={t('live:stationSettlements.noReversalsHint')} />}>
                {(items) => (
                  <ul className="divide-y divide-border">
                    {items.map((reversal) => (
                      <li key={reversal.id} className="flex items-start justify-between gap-3 px-4 py-3 md:px-5">
                        <div className="min-w-0">
                          <p className="font-semibold tabular-nums">
                            <Money value={-reversal.amountPiastres} signDisplay="always" />
                          </p>
                          <p className="mt-1 flex flex-wrap items-center gap-2 text-xs text-muted">
                            <Plate value={reversal.plate} size="sm" />
                            <bdi dir="ltr">{reversal.fuelingReference}</bdi>
                            <Stamp value={reversal.requestedAt} />
                          </p>
                          <p className="mt-1 text-sm">{t('live:settlements.reason', { reason: reversal.reason })}</p>
                        </div>
                        <ReversalStatusChip status={reversal.status} />
                      </li>
                    ))}
                  </ul>
                )}
              </PagedList>
            </ListPanel>
          </PageSection>
        </div>
      </div>
      <BankChangeRequest key={`${changing}-${bank.data?.active?.accountLast4 ?? ''}`} open={changing} onOpenChange={setChanging} stationId={stationId} stationName={stationName} last4={bank.data?.active?.accountLast4} />
    </div>
  );
}

/** What the next payout can be, where it goes, and what holds it. */
function NextPayout({ payable, bank, lastPayout }: { payable: Resource<{ availablePiastres: number }>; bank: Resource<StationBankDetails>; lastPayout?: { amountPiastres: number; valueDate: string } }) {
  const { t } = useTranslation(['station', 'live']);
  const [now] = useState(() => Date.now());
  if (!payable.data || !bank.data) return <Skeleton className="h-40" />;
  const active = bank.data.active;
  const heldUntil = active?.payoutsHeldUntil && new Date(active.payoutsHeldUntil).getTime() > now ? active.payoutsHeldUntil : undefined;
  return (
    <div className="flex flex-col gap-3 rounded-container border border-border bg-surface p-4 shadow-card md:p-5">
      <div>
        <p className="flex items-center gap-2 text-sm text-muted">
          <span aria-hidden className="h-1 w-4 rounded-full bg-accent" />
          {t('live.finance.availableNow')}
        </p>
        <p className="text-kpi font-semibold tabular-nums">
          <Money value={payable.data.availablePiastres} />
        </p>
      </div>
      <dl className="divide-y divide-border border-t border-border text-sm">
        <div className="flex items-center justify-between gap-3 py-2.5">
          <dt className="text-muted">{t('live.finance.paidTo')}</dt>
          <dd className="text-end font-medium">
            {active ? (
              <>
                {active.bankName} · <bdi dir="ltr" className="font-mono">•••• {active.accountLast4}</bdi>
              </>
            ) : (
              <span className="text-warning">{t('live.finance.noAccount')}</span>
            )}
          </dd>
        </div>
        <div className="flex items-center justify-between gap-3 py-2.5">
          <dt className="text-muted">{t('live.finance.lastPayout')}</dt>
          <dd className="text-end font-medium">
            {lastPayout ? (
              <>
                <Money value={lastPayout.amountPiastres} /> · <DayText day={lastPayout.valueDate} />
              </>
            ) : (
              t('live.finance.none')
            )}
          </dd>
        </div>
      </dl>
      {heldUntil && <Banner tone="warning" title={t('live.finance.heldUntil')} description={t('live.finance.heldUntilHint')} action={<Stamp value={heldUntil} className="font-medium" />} />}
      {!active && <Banner tone="warning" title={t('live.finance.noAccount')} description={t('live:finance.bankNoneHint')} />}
    </div>
  );
}

/** The verified account, masked, and a change waiting for approval. */
function BankAccount({ resource }: { resource: Resource<StationBankDetails> }) {
  const { t } = useTranslation(['station', 'live']);
  if (resource.error && !resource.data) return <ErrorState onRetry={resource.reload} className="py-6" />;
  if (!resource.data) return <Skeleton className="h-28" />;
  const { active, pending } = resource.data;
  return (
    <div className="flex flex-col gap-3">
      {pending && <Banner tone="warning" title={t('live:finance.pendingChange')} description={t('live:finance.pendingChangeHint', { bank: pending.bankName, last4: pending.accountLast4 })} />}
      {active ? (
        <div className="flex items-start gap-3 rounded-container border border-border bg-surface p-4 shadow-card">
          <span className="mt-0.5 flex size-10 shrink-0 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
            <Icon icon={Landmark} />
          </span>
          <div className="min-w-0 flex-1">
            <p className="flex flex-wrap items-center gap-2 font-medium">
              {active.bankName}
              <StatusChip tone="success" label={t('live.finance.verified')} />
            </p>
            <p className="text-sm text-muted">{active.holderName}</p>
            <p className="mt-1 text-sm">
              {t('live:finance.accountEnding')} <bdi dir="ltr" className="font-mono">{active.accountLast4}</bdi>
            </p>
            <p className="text-xs text-muted">
              {t('live:finance.letter')} <bdi dir="ltr">{active.bankLetterReference}</bdi>
              {active.decidedAt && (
                <>
                  {' · '}
                  {t('live.finance.verifiedOn')} <Stamp value={active.decidedAt} variant="date" />
                </>
              )}
            </p>
          </div>
        </div>
      ) : (
        <ListPanel>
          <EmptyState icon={Landmark} title={t('live:finance.bankNone')} description={t('live:finance.bankNoneHint')} />
        </ListPanel>
      )}
    </div>
  );
}

/**
 * Asks Operations to change the payout account, as a ticket. The text is prefilled and editable;
 * it asks for the change and promises the bank letter, and warns against typing the account number.
 */
function BankChangeRequest({ open, onOpenChange, stationId, stationName, last4 }: { open: boolean; onOpenChange: (open: boolean) => void; stationId: string; stationName: string; last4?: string }) {
  const { t } = useTranslation(['station', 'live']);
  const navigate = useNavigate();
  const initial = () => ({
    subject: t('live.bankChange.subject', { station: stationName }),
    body: last4 ? t('live.bankChange.body', { station: stationName, last4 }) : t('live.bankChange.bodyNoAccount', { station: stationName }),
  });
  const [form, setForm] = useState(initial);
  const [missing, setMissing] = useState(false);
  // Long digit runs look like an account number or IBAN: refuse them in a ticket.
  const looksLikeAccount = /\d[\d\s-]{9,}\d/.test(form.body) || /EG\d{2}/i.test(form.body.replace(/\s/g, ''));
  return (
    <FormSheet
      open={open}
      onOpenChange={(next) => {
        if (next) setForm(initial());
        setMissing(false);
        onOpenChange(next);
      }}
      title={t('live.bankChange.title')}
      description={t('live.bankChange.description')}
      submitLabel={t('live.bankChange.submit')}
      onSubmit={async () => {
        if (!form.subject.trim() || !form.body.trim() || looksLikeAccount) {
          setMissing(true);
          return false;
        }
        const thread = await stationApi.tickets.open({ type: 'PAYOUT_QUERY', subject: form.subject.trim(), body: form.body.trim(), stationId });
        toast(t('live.bankChange.sent', { reference: thread.ticket.reference }));
        navigate(`/station/tickets/${thread.ticket.id}`);
      }}
    >
      <Banner tone="info" title={t('live.bankChange.howTitle')} description={t('live.bankChange.how')} />
      <TextField label={t('live:tickets.subject')} value={form.subject} maxLength={200} error={missing && !form.subject.trim() ? t('live:errors.required') : undefined} onChange={(event) => setForm({ ...form, subject: event.target.value })} />
      <TextAreaField
        label={t('live:tickets.body')}
        rows={6}
        value={form.body}
        maxLength={4000}
        hint={t('live.bankChange.noNumbers')}
        error={looksLikeAccount ? t('live.bankChange.numberFound') : missing && !form.body.trim() ? t('live:errors.required') : undefined}
        onChange={(event) => setForm({ ...form, body: event.target.value })}
      />
    </FormSheet>
  );
}

/** The statement for a period: balances, the lines, and a CSV of exactly those lines. */
function Statement({ stationId, stationName }: { stationId: string; stationName: string }) {
  const { t } = useTranslation(['station', 'live']);
  const { locale } = useLocale();
  const [range, setRange] = useState<DayRangeValue>(() => periodRange('THIS_MONTH'));
  const [exporting, setExporting] = useState(false);
  const { valid, problem } = useValidRange(range);
  const fromAt = cairoDayStart(valid.from);
  const toAt = cairoDayStart(valid.to, 1);
  const summary = useResource((signal) => statementSummary(stationId, { from: fromAt, to: toAt }, signal), [stationId, fromAt, toAt]);
  const lines = usePagedResource((page, signal) => statementPage(stationId, page, signal), [stationId], 25);
  const inRange = (entry: StationStatementEntry) => (!fromAt || entry.postedAt >= fromAt) && (!toAt || entry.postedAt < toAt);
  const shown = { ...lines, items: lines.items.filter(inRange) };

  const exportCsv = async () => {
    setExporting(true);
    try {
      const rows: StationStatementEntry[] = [];
      let cursor: string | undefined;
      for (let page = 0; page < EXPORT_PAGES; page++) {
        const answer = await statementPage(stationId, { cursor, limit: 100 });
        rows.push(...answer.items.filter(inRange));
        cursor = answer.nextCursor ?? undefined;
        // Newest first: once a page reaches before the start, nothing older is needed.
        if (!cursor || (fromAt && answer.items.some((entry) => entry.postedAt < fromAt))) break;
      }
      const header = (['postedAt', 'type', 'reference', 'plate', 'direction', 'amountEGP', 'balanceAfterEGP'] as const).map((key) => t(`live.statement.csv.${key}`));
      const body = rows.map((entry) =>
        [
          entry.postedAt,
          t(`live:finance.sources.${entry.sourceType}`),
          entry.fueling?.reference ?? entry.payout?.bankReference ?? entry.reference ?? '',
          entry.fueling?.plate ?? '',
          t(`live.statement.direction.${entry.direction}`),
          piastresToCsv(entry.direction === 'CREDIT' ? entry.amountPiastres : -entry.amountPiastres),
          piastresToCsv(entry.balanceAfterPiastres),
        ]
          .map(csvCell)
          .join(','),
      );
      const blob = new Blob(['﻿', [header.map(csvCell).join(','), ...body].join('\r\n')], { type: 'text/csv;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `zawed-statement-${stationName.replace(/\s+/g, '-')}-${valid.from || 'start'}-${valid.to || cairoDay(new Date())}.csv`;
      link.click();
      URL.revokeObjectURL(url);
      toast(t('live.statement.exported', { count: rows.length }));
    } catch {
      toast(t('live:fuelings.exportFailed'), 'danger');
    } finally {
      setExporting(false);
    }
  };

  return (
    <PageSection
      title={t('live:finance.statement')}
      description={t('live.statement.hint')}
      action={
        <Button variant="secondary" size="sm" leadingIcon={Download} loading={exporting} disabled={Boolean(problem)} onClick={() => void exportCsv()}>
          {t('live.statement.export')}
        </Button>
      }
    >
      <ListPanel>
        <div className="flex flex-col gap-3 border-b border-border bg-surface-muted px-4 py-3 md:flex-row md:flex-wrap md:items-start md:px-5 md:py-4">
          <DayRangeFields value={range} onChange={setRange} />
        </div>
        {summary.error && !summary.data ? (
          <ErrorState onRetry={summary.reload} className="py-6" />
        ) : (
          <dl className="grid grid-cols-2 border-b border-border md:grid-cols-4">
            {(['openingPiastres', 'creditsPiastres', 'debitsPiastres', 'closingPiastres'] as const).map((key, index) => (
              <div key={key} className={cn('flex flex-col gap-0.5 px-4 py-3 md:px-5', index % 2 === 1 && 'border-s border-border', index >= 2 && 'border-t border-border md:border-t-0', index === 2 && 'md:border-s')}>
                <dt className="text-xs text-muted">{t(`live.statement.${key}`)}</dt>
                <dd className={cn('font-semibold tabular-nums', key === 'closingPiastres' && 'text-foreground')}>
                  {summary.data && !problem ? <Money value={summary.data[key]} /> : <Skeleton className="h-5 w-20" />}
                </dd>
              </div>
            ))}
          </dl>
        )}
        <PagedList
          resource={shown}
          empty={
            lines.hasMore ? (
              <div className="flex justify-center p-4">
                <Button variant="ghost" loading={lines.loadingMore} onClick={lines.loadMore}>
                  {t('live.statement.older')}
                </Button>
              </div>
            ) : (
              <EmptyState title={t('live.statement.empty')} description={t('live.statement.emptyHint')} />
            )
          }
        >
          {(items) => (
            <ul className="divide-y divide-border">
              {items.map((entry) => {
                const credit = entry.direction === 'CREDIT';
                const held = entry.fueling?.payableHeld && !entry.fueling.holdReleased;
                return (
                  <li key={entry.id} className="flex items-center gap-3 px-4 py-3 md:px-5">
                    <span className={cn('flex size-9 shrink-0 items-center justify-center rounded-full', credit ? 'bg-success-subtle text-success' : 'bg-surface-sunken text-muted')}>
                      <Icon icon={credit ? ArrowDownLeft : ArrowUpRight} size={18} />
                    </span>
                    <div className="min-w-0 flex-1">
                      <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
                        {t(`live:finance.sources.${entry.sourceType}`)}
                        {held && <StatusChip tone="warning" label={t('live:finance.entryHeld')} />}
                        {entry.fueling?.holdReleased && <StatusChip tone="success" label={t('live:finance.entryReleased')} />}
                      </p>
                      <p className="truncate text-xs text-muted">
                        <Stamp value={entry.postedAt} />
                        {entry.fueling && (
                          <>
                            {' · '}
                            <bdi dir="rtl">{entry.fueling.plate}</bdi>
                          </>
                        )}
                        {' · '}
                        <bdi dir="ltr">{entry.fueling?.reference ?? entry.payout?.bankReference ?? entry.reference}</bdi>
                      </p>
                    </div>
                    <div className="text-end">
                      <p className={cn('font-semibold tabular-nums', credit && 'text-success')}>
                        <Money value={credit ? entry.amountPiastres : -entry.amountPiastres} signDisplay="always" />
                      </p>
                      <p className="text-xs text-muted tabular-nums">{t('live:finance.balanceAfter', { amount: formatMoney(entry.balanceAfterPiastres, locale) })}</p>
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </PagedList>
      </ListPanel>
    </PageSection>
  );
}

/** Choose one of the operator's stations, then see its money. */
export function StationSettlementsPage() {
  const { t } = useTranslation(['station', 'live']);
  const { locale } = useLocale();
  const [chosen, setChosen] = useState<string>();
  const stations = useResource((signal) => stationApi.stations({ limit: 100 }, signal), []);
  const items = stations.data?.items ?? [];
  const station = items.find((item) => item.id === chosen) ?? items[0];
  const name = (item: (typeof items)[number]) => (locale === 'ar' ? item.nameAr : item.nameEn);

  return (
    <>
      <PageHeader
        title={t('live.finance.title')}
        description={t('live.finance.description')}
        actions={
          items.length > 1 && station ? (
            <SelectField
              className="w-full md:w-64"
              label={t('live.filters.station')}
              value={station.id}
              onValueChange={setChosen}
              options={items.map((item) => ({ value: item.id, label: name(item) }))}
            />
          ) : undefined
        }
      />
      {stations.error && !stations.data ? (
        <ErrorState onRetry={stations.reload} />
      ) : !stations.data ? (
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4" aria-busy>
          {Array.from({ length: 4 }, (_, index) => (
            <Skeleton key={index} className="h-24" />
          ))}
        </div>
      ) : !station ? (
        <ListPanel>
          <EmptyState title={t('live:stationSettlements.noStations')} description={t('live:stationSettlements.noStationsHint')} />
        </ListPanel>
      ) : (
        <StationFinance key={station.id} stationId={station.id} stationName={name(station)} />
      )}
    </>
  );
}
