/**
 * Support tickets, shared by the three portals. A company or a station opens a ticket and talks to
 * Operations in its thread; Operations works the queue, writes internal notes nobody else sees, and
 * moves a ticket through open, in progress, resolved and closed. What each side may open and see is
 * decided by the API; the portal only chooses which endpoints to call.
 */
import { Plus, Send } from 'lucide-react';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router';
import {
  Banner,
  Button,
  DataView,
  DateTimeText,
  ErrorState,
  ListRow,
  SegmentedControl,
  SelectField,
  Skeleton,
  fieldControlClass,
  StatusChip,
  TextField,
  toast,
  FilterBar,
  type DataColumn,
} from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { BackLink, DetailList, ListPanel, PageHeader } from '@/shell/page';
import {
  companyApi,
  COMPANY_TICKET_TYPES,
  opsApi,
  stationApi,
  STATION_TICKET_TYPES,
  TICKET_STATUSES,
  TICKET_TYPES,
  type Page,
  type PageQuery,
  type Ticket,
  type TicketStatus,
  type TicketThread,
  type TicketType,
  type UserRole,
} from './api';
import { TicketStatusChip } from './chips';
import { EmptyList, FormSheet, PagedList } from './components';
import { liveError } from './errors';
import { usePagedResource, useResource } from './use-resource';

export type TicketPortal = 'company' | 'station' | 'ops';

const BASE: Record<TicketPortal, string> = { company: '/company', station: '/station', ops: '/ops' };

type StatusFilter = 'ALL' | TicketStatus;
type TypeFilter = 'ALL' | TicketType;

interface ListQuery extends PageQuery {
  status?: TicketStatus;
  type?: TicketType;
}

const listTickets = (portal: TicketPortal, q: ListQuery, signal: AbortSignal): Promise<Page<Ticket>> =>
  portal === 'ops'
    ? opsApi.tickets.list(q, signal)
    : portal === 'company'
      ? companyApi.tickets.list({ cursor: q.cursor, limit: q.limit, status: q.status }, signal)
      : stationApi.tickets.list({ cursor: q.cursor, limit: q.limit, status: q.status }, signal);

const getTicket = (portal: TicketPortal, id: string, signal: AbortSignal): Promise<TicketThread> =>
  portal === 'ops' ? opsApi.tickets.get(id, signal) : portal === 'company' ? companyApi.tickets.get(id, signal) : stationApi.tickets.get(id, signal);

const replyTicket = (portal: TicketPortal, id: string, body: string, internal: boolean): Promise<TicketThread> =>
  portal === 'ops' ? opsApi.tickets.reply(id, body, internal) : portal === 'company' ? companyApi.tickets.reply(id, body) : stationApi.tickets.reply(id, body);

/** A multi-line text box that looks like the design system's text field. */
function MessageBox({
  label,
  value,
  onChange,
  error,
  disabled,
  rows = 5,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  error?: string;
  disabled?: boolean;
  rows?: number;
}) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-label font-medium">
        {label}
      </label>
      <textarea
        id={id}
        rows={rows}
        maxLength={4000}
        disabled={disabled}
        value={value}
        aria-invalid={error ? true : undefined}
        onChange={(event) => onChange(event.target.value)}
        className={cn(fieldControlClass(Boolean(error)), 'resize-y px-3 py-2.5 leading-relaxed')}
      />
      {error && <p className="text-xs font-medium text-danger">{error}</p>}
    </div>
  );
}

/** A company or station opens a ticket; the thread opens as soon as it is sent. */
function NewTicketSheet({ portal, onClose }: { portal: Exclude<TicketPortal, 'ops'>; onClose: () => void }) {
  const { t } = useTranslation('live');
  const navigate = useNavigate();
  const types = portal === 'company' ? COMPANY_TICKET_TYPES : STATION_TICKET_TYPES;
  const [type, setType] = useState<TicketType>(types[0] ?? 'OTHER');
  const [subject, setSubject] = useState('');
  const [body, setBody] = useState('');
  const [errors, setErrors] = useState<{ subject?: string; body?: string }>({});
  return (
    <FormSheet
      open
      onOpenChange={onClose}
      title={t('tickets.newTitle')}
      description={t('tickets.newHint')}
      submitLabel={t('tickets.submit')}
      onSubmit={async () => {
        const found = {
          subject: subject.trim() ? undefined : t('errors.required'),
          body: body.trim() ? undefined : t('errors.required'),
        };
        if (found.subject || found.body) {
          setErrors(found);
          return false;
        }
        const request = { type, subject: subject.trim(), body: body.trim() };
        const thread = await (portal === 'company' ? companyApi.tickets.open(request) : stationApi.tickets.open(request));
        toast(t('tickets.created'));
        navigate(`${BASE[portal]}/tickets/${thread.ticket.id}`);
      }}
      onError={(failure) => {
        if (failure.field === 'subject') setErrors({ subject: failure.message });
        if (failure.field === 'body') setErrors({ body: failure.message });
      }}
    >
      <SelectField<TicketType>
        label={t('tickets.typeLabel')}
        value={type}
        onValueChange={setType}
        options={types.map((value) => ({ value, label: t(`tickets.type.${value}`) }))}
      />
      <TextField
        label={t('tickets.subject')}
        maxLength={200}
        value={subject}
        error={errors.subject}
        onChange={(event) => {
          setSubject(event.target.value);
          setErrors({});
        }}
      />
      <MessageBox
        label={t('tickets.body')}
        value={body}
        error={errors.body}
        onChange={(value) => {
          setBody(value);
          setErrors({});
        }}
      />
    </FormSheet>
  );
}

/** The ticket list: a company's or station's own, or Operations' queue with its filters. */
export function TicketsPage({ portal }: { portal: TicketPortal }) {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const navigate = useNavigate();
  const ops = portal === 'ops';
  const [status, setStatus] = useState<StatusFilter>(ops ? 'OPEN' : 'ALL');
  const [type, setType] = useState<TypeFilter>('ALL');
  const [creating, setCreating] = useState(false);
  const tickets = usePagedResource(
    (page, signal) =>
      listTickets(portal, { ...page, status: status === 'ALL' ? undefined : status, type: type === 'ALL' ? undefined : type }, signal),
    [portal, status, type],
  );
  // Names for who a ticket is from (Operations only): the first 100 cover a pilot.
  const companies = useResource((signal) => (ops ? opsApi.companies.list({ limit: 100 }, signal) : Promise.resolve(undefined)), [ops]);
  const operators = useResource((signal) => (ops ? opsApi.operators.list({ limit: 100 }, signal) : Promise.resolve(undefined)), [ops]);
  const from = (ticket: Ticket) => {
    const company = ticket.companyId ? companies.data?.items.find((item) => item.id === ticket.companyId) : undefined;
    if (company) return locale === 'ar' ? company.nameAr : company.nameEn;
    const operator = ticket.operatorId ? operators.data?.items.find((item) => item.id === ticket.operatorId) : undefined;
    if (operator) return locale === 'ar' ? operator.nameAr : operator.nameEn;
    return t(`tickets.scope.${ticket.scopeType}`);
  };
  const open = (ticket: Ticket) => navigate(`${BASE[portal]}/tickets/${ticket.id}`);

  const columns: DataColumn<Ticket>[] = [
    { id: 'reference', header: t('tickets.reference'), cell: (ticket) => <span dir="ltr">{ticket.reference}</span> },
    { id: 'subject', header: t('tickets.subject'), cell: (ticket) => <span className="font-medium">{ticket.subject}</span> },
    { id: 'type', header: t('tickets.typeLabel'), cell: (ticket) => t(`tickets.type.${ticket.type}`) },
    ...(ops ? [{ id: 'from', header: t('tickets.from'), cell: from } satisfies DataColumn<Ticket>] : []),
    { id: 'updated', header: t('tickets.updated'), cell: (ticket) => <DateTimeText value={ticket.updatedAt} /> },
    { id: 'status', header: t('vehicle.status'), cell: (ticket) => <TicketStatusChip status={ticket.status} /> },
  ];

  const filtered = status !== 'ALL' || type !== 'ALL';
  return (
    <>
      <PageHeader
        title={t('tickets.title')}
        description={t(`tickets.${portal}Description`)}
        actions={
          !ops && (
            <Button leadingIcon={Plus} onClick={() => setCreating(true)}>
              {t('tickets.new')}
            </Button>
          )
        }
      />
      <ListPanel>
        <FilterBar>
          <SelectField<StatusFilter>
            className="md:w-56"
            label={t('tickets.statusFilter')}
            value={status}
            onValueChange={setStatus}
            options={[{ value: 'ALL', label: t('common.all') }, ...TICKET_STATUSES.map((value) => ({ value, label: t(`tickets.status.${value}`) }))]}
          />
          {ops && (
            <SelectField<TypeFilter>
              className="md:w-56"
              label={t('tickets.typeFilter')}
              value={type}
              onValueChange={setType}
              options={[{ value: 'ALL', label: t('common.all') }, ...TICKET_TYPES.map((value) => ({ value, label: t(`tickets.type.${value}`) }))]}
            />
          )}
        </FilterBar>
        <PagedList
          resource={tickets}
          empty={
            <EmptyList
              title={t(filtered ? 'tickets.noResults' : 'tickets.empty')}
              description={t(filtered ? 'tickets.noResultsHint' : `tickets.${portal}EmptyHint`)}
            />
          }
        >
          {(items) => (
            <DataView
              caption={t('tickets.title')}
              rows={items}
              columns={columns}
              getRowId={(ticket) => ticket.id}
              onRowClick={open}
              tableFrom="lg"
              renderListItem={(ticket) => (
                <ListRow
                  title={ticket.subject}
                  subtitle={
                    <>
                      <span dir="ltr">{ticket.reference}</span>
                      {' · '}
                      {t(`tickets.type.${ticket.type}`)}
                      {ops && ` · ${from(ticket)}`}
                    </>
                  }
                  trailing={<TicketStatusChip status={ticket.status} />}
                  onClick={() => open(ticket)}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
      {creating && portal !== 'ops' && <NewTicketSheet portal={portal} onClose={() => setCreating(false)} />}
    </>
  );
}

/** One message in the thread; an internal note (Operations only) is set apart and labelled. */
function MessageCard({ message }: { message: TicketThread['messages'][number] }) {
  const { t } = useTranslation('live');
  return (
    <li
      className={cn(
        'flex flex-col gap-2 rounded-container border p-4',
        message.internal ? 'border-warning bg-warning-subtle' : 'border-border bg-surface',
      )}
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
        <span className="font-medium text-foreground">{t(`enums.userRole.${message.authorRole as UserRole}`)}</span>
        <DateTimeText value={message.createdAt} />
        {message.internal && <StatusChip tone="warning" label={t('tickets.internalNote')} />}
      </div>
      <p className="text-sm whitespace-pre-wrap">{message.body}</p>
    </li>
  );
}

/** Operations' status moves: take it, resolve, close, reopen. */
function StatusActions({ ticket, onChanged }: { ticket: Ticket; onChanged: () => void }) {
  const { t } = useTranslation('live');
  const [busy, setBusy] = useState<string>();
  const move = async (key: string, status: TicketStatus, assignToMe?: boolean) => {
    setBusy(key);
    try {
      await opsApi.tickets.setStatus(ticket.id, status, assignToMe);
      toast(t('tickets.statusChanged'));
      onChanged();
    } catch (error) {
      toast(liveError(t, error).message, 'danger');
    } finally {
      setBusy(undefined);
    }
  };
  const { status } = ticket;
  return (
    <>
      {status === 'OPEN' && (
        <Button loading={busy === 'take'} onClick={() => void move('take', 'IN_PROGRESS', true)}>
          {t('tickets.take')}
        </Button>
      )}
      {(status === 'OPEN' || status === 'IN_PROGRESS') && (
        <Button variant="secondary" loading={busy === 'resolve'} onClick={() => void move('resolve', 'RESOLVED')}>
          {t('tickets.resolve')}
        </Button>
      )}
      {status !== 'CLOSED' && (
        <Button variant="ghost" loading={busy === 'close'} onClick={() => void move('close', 'CLOSED')}>
          {t('tickets.close')}
        </Button>
      )}
      {(status === 'RESOLVED' || status === 'CLOSED') && (
        <Button variant="secondary" loading={busy === 'reopen'} onClick={() => void move('reopen', 'OPEN')}>
          {t('tickets.reopen')}
        </Button>
      )}
    </>
  );
}

type ReplyKind = 'REPLY' | 'NOTE';

/** The reply box. Closed tickets take no replies; Operations can switch a reply to an internal note. */
function ReplyBox({ portal, ticket, onSent }: { portal: TicketPortal; ticket: Ticket; onSent: () => void }) {
  const { t } = useTranslation('live');
  const [body, setBody] = useState('');
  const [kind, setKind] = useState<ReplyKind>('REPLY');
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string>();
  const closed = ticket.status === 'CLOSED';

  const send = async () => {
    if (!body.trim()) {
      setProblem(t('errors.required'));
      return;
    }
    setBusy(true);
    setProblem(undefined);
    try {
      await replyTicket(portal, ticket.id, body.trim(), portal === 'ops' && kind === 'NOTE');
      setBody('');
      toast(t(kind === 'NOTE' ? 'tickets.noteSent' : 'tickets.sent'));
      onSent();
    } catch (error) {
      setProblem(liveError(t, error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-col gap-3">
      {closed && <Banner tone="info" title={t('tickets.closedBanner')} description={t(`tickets.${portal === 'ops' ? 'closedHintOps' : 'closedHint'}`)} />}
      {portal === 'ops' && (
        <SegmentedControl<ReplyKind>
          label={t('tickets.replyKindLabel')}
          value={kind}
          onValueChange={setKind}
          options={[
            { value: 'REPLY', label: t('tickets.replyKind') },
            { value: 'NOTE', label: t('tickets.noteKind') },
          ]}
        />
      )}
      <MessageBox
        label={kind === 'NOTE' ? t('tickets.noteLabel') : t('tickets.replyLabel')}
        value={body}
        disabled={closed}
        error={problem}
        onChange={(value) => {
          setBody(value);
          setProblem(undefined);
        }}
        rows={4}
      />
      <div>
        <Button leadingIcon={Send} loading={busy} disabled={closed} onClick={() => void send()}>
          {kind === 'NOTE' ? t('tickets.addNote') : t('tickets.send')}
        </Button>
      </div>
    </div>
  );
}

/** One ticket: its facts, the thread, the reply box and, for Operations, the status buttons. */
export function TicketThreadPage({ portal }: { portal: TicketPortal }) {
  const { id = '' } = useParams();
  const { t } = useTranslation('live');
  const ops = portal === 'ops';
  const back = `${BASE[portal]}/tickets`;
  const thread = useResource((signal) => getTicket(portal, id, signal), [portal, id]);

  if (thread.error && !thread.data) {
    return (
      <>
        <BackLink to={back} label={t('tickets.back')} />
        <ErrorState onRetry={thread.reload} />
      </>
    );
  }
  if (!thread.data) {
    return (
      <div className="flex flex-col gap-4" aria-busy>
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  const { ticket, messages } = thread.data;
  return (
    <>
      <BackLink to={back} label={t('tickets.back')} />
      <PageHeader
        title={ticket.subject}
        description={`${ticket.reference} · ${t(`tickets.type.${ticket.type}`)}`}
        actions={ops && <StatusActions ticket={ticket} onChanged={thread.reload} />}
      />
      <div className="grid grid-cols-1 gap-8 lg:grid-cols-5">
        <div className="flex min-w-0 flex-col gap-6 lg:col-span-3">
          <ul className="flex flex-col gap-3" aria-label={t('tickets.messages')}>
            {messages.map((message) => (
              <MessageCard key={message.id} message={message} />
            ))}
          </ul>
          <ReplyBox portal={portal} ticket={ticket} onSent={thread.reload} />
        </div>
        <div className="lg:col-span-2">
          <ListPanel>
            <DetailList
              rows={[
                { label: t('vehicle.status'), value: <TicketStatusChip status={ticket.status} /> },
                { label: t('tickets.typeLabel'), value: t(`tickets.type.${ticket.type}`) },
                { label: t('tickets.opened'), value: <DateTimeText value={ticket.createdAt} /> },
                { label: t('tickets.updated'), value: <DateTimeText value={ticket.updatedAt} /> },
                ticket.resolvedAt && { label: t('tickets.resolvedAt'), value: <DateTimeText value={ticket.resolvedAt} /> },
                ops &&
                  ticket.companyId && {
                    label: t('tickets.from'),
                    value: (
                      <Link to={`/ops/companies/${ticket.companyId}`} className="text-primary hover:underline">
                        {t('tickets.openCompany')}
                      </Link>
                    ),
                  },
                ops &&
                  ticket.operatorId && {
                    label: t('tickets.from'),
                    value: (
                      <Link to={`/ops/operators/${ticket.operatorId}`} className="text-primary hover:underline">
                        {t('tickets.openOperator')}
                      </Link>
                    ),
                  },
                ops &&
                  ticket.fuelingId && {
                    label: t('tickets.fueling'),
                    value: (
                      <Link to={`/ops/transactions/${ticket.fuelingId}`} className="text-primary hover:underline">
                        {t('alerts.openFueling')}
                      </Link>
                    ),
                  },
              ]}
            />
          </ListPanel>
        </div>
      </div>
    </>
  );
}
