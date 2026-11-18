import { Landmark, Send } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, TextField, toast } from '@/design-system';
import { ListPanel, PageSection } from '@/shell/page';
import { opsApi } from '../api';
import { FormSheet } from '../components';
import { BankAccountPanel, PayableCards, StationStatementList } from '../finance';
import { optional, parsePounds } from '../input';
import { usePagedResource, useResource } from '../use-resource';

/** Today in Cairo as yyyy-mm-dd: the default value date of a payout. */
function todayInCairo(): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Africa/Cairo' }).format(new Date());
}

/**
 * A new bank account for the station, from the bank's letter. The account number is typed here once:
 * the API stores it and only ever returns its last four characters, so nothing here can show it back.
 */
function BankChangeSheet({ stationId, onClose, onDone }: { stationId: string; onClose: () => void; onDone: () => void }) {
  const { t } = useTranslation('live');
  const [accountNumber, setAccountNumber] = useState('');
  const [bankName, setBankName] = useState('');
  const [holderName, setHolderName] = useState('');
  const [letter, setLetter] = useState('');
  const [errors, setErrors] = useState<Record<string, string | undefined>>({});
  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={t('finance.bankChange.title')}
      description={t('finance.bankChange.hint')}
      submitLabel={t('finance.bankChange.submit')}
      onSubmit={async () => {
        const required = t('errors.required');
        const found = {
          accountNumber: accountNumber.trim() ? undefined : required,
          bankName: bankName.trim() ? undefined : required,
          holderName: holderName.trim() ? undefined : required,
          bankLetterReference: letter.trim() ? undefined : required,
        };
        if (Object.values(found).some(Boolean)) {
          setErrors(found);
          return false;
        }
        await opsApi.finance.requestBankChange(stationId, {
          accountNumber: accountNumber.trim(),
          bankName: bankName.trim(),
          holderName: holderName.trim(),
          bankLetterReference: letter.trim(),
        });
        // The number leaves the form as soon as it is sent.
        setAccountNumber('');
        toast(t('finance.bankChange.done'));
        onDone();
      }}
      onError={(failure) => {
        if (failure.field) setErrors({ [failure.field]: failure.message });
      }}
    >
      <TextField
        label={t('finance.bankChange.accountNumber')}
        hint={t('finance.bankChange.accountNumberHint')}
        dir="ltr"
        autoComplete="off"
        spellCheck={false}
        maxLength={64}
        value={accountNumber}
        error={errors.accountNumber}
        onChange={(event) => setAccountNumber(event.target.value)}
      />
      <TextField label={t('finance.bankChange.bankName')} maxLength={120} value={bankName} error={errors.bankName} onChange={(event) => setBankName(event.target.value)} />
      <TextField
        label={t('finance.bankChange.holderName')}
        hint={t('finance.bankChange.holderNameHint')}
        maxLength={120}
        value={holderName}
        error={errors.holderName}
        onChange={(event) => setHolderName(event.target.value)}
      />
      <TextField
        label={t('finance.bankChange.letter')}
        hint={t('finance.bankChange.letterHint')}
        dir="ltr"
        maxLength={64}
        autoComplete="off"
        value={letter}
        error={errors.bankLetterReference}
        onChange={(event) => setLetter(event.target.value)}
      />
    </FormSheet>
  );
}

/** Record a bank transfer to the station. Above the approval threshold it waits for a second person. */
function PayoutSheet({ stationId, onClose, onDone }: { stationId: string; onClose: () => void; onDone: () => void }) {
  const { t } = useTranslation('live');
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');
  const [valueDate, setValueDate] = useState(todayInCairo());
  const [note, setNote] = useState('');
  const [errors, setErrors] = useState<{ amount?: string; reference?: string; valueDate?: string }>({});
  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={t('finance.payout.title')}
      description={t('finance.payout.hint')}
      submitLabel={t('finance.payout.submit')}
      onSubmit={async () => {
        const piastres = parsePounds(amount);
        const found = {
          amount: piastres === null || piastres <= 0 ? t('errors.invalidAmount') : undefined,
          reference: reference.trim() ? undefined : t('errors.required'),
          valueDate: valueDate ? undefined : t('errors.required'),
        };
        if (found.amount || found.reference || found.valueDate) {
          setErrors(found);
          return false;
        }
        const payout = await opsApi.finance.recordPayout(stationId, {
          amountPiastres: piastres ?? 0,
          bankReference: reference.trim(),
          valueDate,
          note: optional(note),
        });
        toast(t(payout.status === 'PENDING' ? 'finance.payout.doneApproval' : 'finance.payout.done'));
        onDone();
      }}
      onError={(failure) => {
        if (failure.field === 'bankReference') setErrors({ reference: failure.message });
        if (failure.field === 'valueDate') setErrors({ valueDate: failure.message });
        if (failure.field === 'amountPiastres') setErrors({ amount: failure.message });
      }}
    >
      <TextField
        label={t('finance.payout.amount')}
        inputMode="decimal"
        dir="ltr"
        value={amount}
        error={errors.amount}
        onChange={(event) => setAmount(event.target.value)}
      />
      <TextField
        label={t('finance.payout.bankReference')}
        hint={t('finance.payout.bankReferenceHint')}
        dir="ltr"
        maxLength={64}
        autoComplete="off"
        spellCheck={false}
        value={reference}
        error={errors.reference}
        onChange={(event) => setReference(event.target.value)}
      />
      <TextField
        label={t('finance.payout.valueDate')}
        hint={t('finance.payout.valueDateHint')}
        type="date"
        dir="ltr"
        value={valueDate}
        error={errors.valueDate}
        onChange={(event) => setValueDate(event.target.value)}
      />
      <TextField label={t('common.optional', { label: t('finance.payout.note') })} maxLength={500} value={note} onChange={(event) => setNote(event.target.value)} />
    </FormSheet>
  );
}

type Dialog = 'bank' | 'payout' | undefined;

/** Ops, on a station page: what the platform owes the station, where it pays, and every movement. A closed station is still paid what it is owed. */
export function FinanceSection({ stationId }: { stationId: string }) {
  const { t } = useTranslation('live');
  const [dialog, setDialog] = useState<Dialog>();
  const payable = useResource((signal) => opsApi.finance.payable(stationId, signal), [stationId]);
  const bank = useResource((signal) => opsApi.finance.bankAccount(stationId, signal), [stationId]);
  const statement = usePagedResource((page, signal) => opsApi.finance.statement(stationId, page, signal), [stationId]);
  const close = () => setDialog(undefined);
  const refresh = () => {
    payable.reload();
    bank.reload();
    statement.reload();
  };
  // A payout needs a verified account; the API refuses without one and says why, but the button
  // should not invite it.
  const canPay = Boolean(bank.data?.active);

  return (
    <>
      <PageSection
        title={t('finance.title')}
        action={
          <Button size="sm" leadingIcon={Send} disabled={!canPay} onClick={() => setDialog('payout')}>
            {t('finance.recordPayout')}
          </Button>
        }
      >
        <PayableCards resource={payable} />
        <div className="grid grid-cols-1 gap-8 lg:grid-cols-5">
          <div className="flex min-w-0 flex-col gap-3 lg:col-span-2">
            <h3 className="text-sm font-semibold">{t('finance.bank')}</h3>
            <BankAccountPanel
              resource={bank}
              actions={
                <Button variant="secondary" size="sm" leadingIcon={Landmark} disabled={Boolean(bank.data?.pending)} onClick={() => setDialog('bank')}>
                  {t('finance.requestBankChange')}
                </Button>
              }
            />
          </div>
          <div className="flex min-w-0 flex-col gap-3 lg:col-span-3">
            <h3 className="text-sm font-semibold">{t('finance.statement')}</h3>
            <ListPanel>
              <StationStatementList resource={statement} />
            </ListPanel>
          </div>
        </div>
      </PageSection>
      {dialog === 'bank' && <BankChangeSheet stationId={stationId} onClose={close} onDone={refresh} />}
      {dialog === 'payout' && <PayoutSheet stationId={stationId} onClose={close} onDone={refresh} />}
    </>
  );
}
