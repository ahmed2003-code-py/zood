import { Building2, Mail, MessageCircle, Phone } from 'lucide-react';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '@/api';
import type { Lead } from '@/api/types';
import { Button, DataView, DateTimeText, Icon, ListRow, SelectField, StatusChip, toast, type DataColumn } from '@/design-system';
import { LEAD_STATUSES, type LeadStatus } from '@/domain/demo-request';
import { cn } from '@/lib/cn';
import { DetailList, ListPanel, PageHeader } from '@/shell/page';
import { EmptyList, FormSheet, PagedList } from '../components';
import { usePagedResource, type PagedResource } from '../use-resource';
import { LEAD_FILTERS, LEAD_NOTE_MAX, LEAD_TONE, leadQuery, leadUpdate, whatsappLink, type LeadFilter } from './leads-model';

/**
 * Ops: requests from the landing page's "Request a demo" form. Platform roles only: the Ops
 * portal admits only them, and the API refuses everyone else.
 *
 * Unlike the other src/live screens this one goes through `api` (src/api), so it works in the
 * in-browser demo too, where the landing form stores its requests in the mock.
 */
export function LeadsPage() {
  const { t } = useTranslation('live');
  const [filter, setFilter] = useState<LeadFilter>('NEW');
  const [selected, setSelected] = useState<Lead>();
  const leads = usePagedResource(
    (page, signal) => api.leads.list(leadQuery(filter, page), signal).then((result) => ({ items: result.items, nextCursor: result.nextCursor ?? undefined })),
    [filter],
  );

  return (
    <>
      <PageHeader title={t('leads.title')} description={t('leads.description')} />
      <div className="mb-4 flex flex-col gap-3 md:flex-row md:items-end">
        <SelectField<LeadFilter>
          className="md:w-56"
          label={t('leads.statusFilter')}
          value={filter}
          onValueChange={setFilter}
          options={LEAD_FILTERS.map((value) => ({ value, label: value === 'ALL' ? t('common.all') : t(`leads.status.${value}`) }))}
        />
      </div>
      <ListPanel>
        <LeadsList resource={leads} filter={filter} onSelect={setSelected} />
      </ListPanel>
      {selected && (
        <LeadSheet
          key={selected.id}
          lead={selected}
          onClose={() => setSelected(undefined)}
          onSaved={() => {
            setSelected(undefined);
            leads.reload();
          }}
        />
      )}
    </>
  );
}

/** The list in its four states: loading, error, empty (with or without a filter) and loaded. */
export function LeadsList({ resource, filter, onSelect }: { resource: PagedResource<Lead>; filter: LeadFilter; onSelect: (lead: Lead) => void }) {
  const { t } = useTranslation('live');
  const columns: DataColumn<Lead>[] = [
    { id: 'reference', header: t('leads.columns.reference'), cell: (lead) => <span dir="ltr" className="font-medium tabular-nums">{lead.reference}</span> },
    {
      id: 'company',
      header: t('leads.columns.company'),
      cell: (lead) => (
        <div className="min-w-0">
          <p className="font-medium">{lead.companyName}</p>
          <p className="text-xs text-muted">{lead.contactName}</p>
        </div>
      ),
    },
    { id: 'contact', header: t('leads.columns.contact'), cell: (lead) => <span dir="ltr" className="tabular-nums">{lead.phone}</span> },
    { id: 'fleetSize', header: t('leads.columns.fleetSize'), cell: (lead) => t(`leads.fleetSize.${lead.fleetSize}`) },
    { id: 'status', header: t('leads.columns.status'), cell: (lead) => <LeadStatusChip status={lead.status} /> },
    { id: 'received', header: t('leads.columns.received'), align: 'end', cell: (lead) => <DateTimeText value={lead.createdAt} /> },
  ];

  return (
    <PagedList
      resource={resource}
      empty={
        filter === 'ALL' ? (
          <EmptyList title={t('leads.empty')} description={t('leads.emptyHint')} />
        ) : (
          <EmptyList title={t('leads.emptyFiltered')} description={t('leads.emptyFilteredHint')} />
        )
      }
    >
      {(items) => (
        <DataView
          caption={t('leads.title')}
          rows={items}
          columns={columns}
          getRowId={(lead) => lead.id}
          onRowClick={onSelect}
          tableFrom="md"
          renderListItem={(lead) => (
            <ListRow
              leading={
                <span className="flex size-9 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
                  <Icon icon={Building2} size={18} />
                </span>
              }
              title={lead.companyName}
              subtitle={`${lead.contactName}${t('common.separator')}${t(`leads.fleetSize.${lead.fleetSize}`)}`}
              trailing={<LeadStatusChip status={lead.status} />}
              trailingSub={<DateTimeText value={lead.createdAt} variant="relative" />}
              onClick={() => onSelect(lead)}
            />
          )}
        />
      )}
    </PagedList>
  );
}

export function LeadStatusChip({ status }: { status: LeadStatus }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={LEAD_TONE[status]} label={t(`leads.status.${status}`)} />;
}

/** One request: how to reach the contact, then its status and note. */
function LeadSheet({ lead, onClose, onSaved }: { lead: Lead; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation('live');
  const noteId = useId();
  const [status, setStatus] = useState<LeadStatus>(lead.status);
  const [note, setNote] = useState(lead.note ?? '');
  const tooLong = note.trim().length > LEAD_NOTE_MAX;

  return (
    <FormSheet
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      title={lead.companyName}
      description={lead.reference}
      submitLabel={t('leads.save')}
      onSubmit={async () => {
        if (tooLong) return false;
        const body = leadUpdate(lead, status, note);
        if (body) {
          await api.leads.update(lead.id, body);
          toast(t('leads.saved'));
        }
        onSaved();
      }}
    >
      <div className="overflow-hidden rounded-container border border-border bg-surface">
        <DetailList
          rows={[
            { label: t('leads.details.contactName'), value: lead.contactName },
            { label: t('leads.details.phone'), value: <span dir="ltr" className="tabular-nums">{lead.phone}</span> },
            { label: t('leads.details.email'), value: <span dir="ltr" className="break-all">{lead.email}</span> },
            { label: t('leads.columns.fleetSize'), value: t(`leads.fleetSize.${lead.fleetSize}`) },
            { label: t('leads.details.language'), value: t(`leads.locale.${lead.locale}`) },
            { label: t('leads.details.received'), value: <DateTimeText value={lead.createdAt} /> },
            lead.updatedAt !== lead.createdAt && { label: t('leads.details.updated'), value: <DateTimeText value={lead.updatedAt} /> },
          ]}
        />
      </div>
      <div className="grid grid-cols-3 gap-2">
        <Button asChild variant="secondary" size="sm" leadingIcon={Phone}>
          <a href={`tel:${lead.phone}`}>{t('leads.call')}</a>
        </Button>
        <Button asChild variant="secondary" size="sm" leadingIcon={MessageCircle}>
          <a href={whatsappLink(lead.phone)} target="_blank" rel="noopener noreferrer">
            {t('leads.whatsapp')}
          </a>
        </Button>
        <Button asChild variant="secondary" size="sm" leadingIcon={Mail}>
          <a href={`mailto:${lead.email}`}>{t('leads.email')}</a>
        </Button>
      </div>
      <p className="text-sm text-muted">{t('leads.updateHint')}</p>
      <SelectField<LeadStatus>
        label={t('leads.newStatus')}
        value={status}
        onValueChange={setStatus}
        options={LEAD_STATUSES.map((value) => ({ value, label: t(`leads.status.${value}`) }))}
      />
      <div className="flex flex-col gap-1.5">
        <label htmlFor={noteId} className="text-sm font-medium">
          {t('leads.note')}
        </label>
        <textarea
          id={noteId}
          rows={4}
          value={note}
          aria-invalid={tooLong || undefined}
          aria-describedby={`${noteId}-hint`}
          onChange={(event) => setNote(event.target.value)}
          className={cn(
            'w-full rounded-control border bg-input px-3 py-2.5 text-foreground transition-colors focus-visible:border-focus-ring',
            tooLong ? 'border-danger' : 'border-input-border',
          )}
        />
        <p id={`${noteId}-hint`} className={cn('text-xs', tooLong ? 'font-medium text-danger' : 'text-muted')}>
          {tooLong ? t('leads.noteTooLong') : t('leads.noteHint')}
        </p>
      </div>
    </FormSheet>
  );
}
