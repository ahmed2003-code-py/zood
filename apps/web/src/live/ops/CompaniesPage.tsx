import { Building2, Plus, Search } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { Button, DataView, Icon, ListRow, SegmentedControl, TextField, type DataColumn } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { ListPanel, PageHeader } from '@/shell/page';
import { opsApi, type Company, type CompanyStatus } from '../api';
import { CompanyStatusChip } from '../chips';
import { EmptyList, PagedList } from '../components';
import { useDebounced, usePagedResource } from '../use-resource';
import { CompanyFormSheet } from './company-form';

type StatusFilter = 'ALL' | CompanyStatus;

/** Ops: every fleet company, searchable, newest first; onboarding a new one opens its page. */
export function CompaniesPage() {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<StatusFilter>('ALL');
  const [creating, setCreating] = useState(false);
  const query = useDebounced(search.trim());
  const companies = usePagedResource(
    (page, signal) => opsApi.companies.list({ ...page, q: query || undefined, status: status === 'ALL' ? undefined : status }, signal),
    [query, status],
  );
  const filtered = Boolean(query) || status !== 'ALL';
  const name = (company: Company) => (locale === 'ar' ? company.nameAr : company.nameEn);
  const open = (company: Company) => navigate(`/ops/companies/${company.id}`);

  const columns: DataColumn<Company>[] = [
    {
      id: 'company',
      header: t('companies.columns.company'),
      cell: (company) => (
        <div className="min-w-0">
          <p className="font-medium">{name(company)}</p>
          <p className="text-xs text-muted">{company.legalName}</p>
        </div>
      ),
    },
    { id: 'taxId', header: t('companies.columns.taxId'), cell: (company) => <span dir="ltr">{company.taxId ?? t('common.notSet')}</span> },
    {
      id: 'cooldown',
      header: t('companies.columns.cooldown'),
      align: 'end',
      cell: (company) => t('common.minutes', { count: company.cooldownMinutes }),
    },
    { id: 'status', header: t('companies.columns.status'), cell: (company) => <CompanyStatusChip status={company.status} /> },
  ];

  return (
    <>
      <PageHeader
        title={t('companies.title')}
        description={t('companies.description')}
        actions={
          <Button leadingIcon={Plus} onClick={() => setCreating(true)}>
            {t('companies.new')}
          </Button>
        }
      />
      <div className="mb-4 flex flex-col gap-3 md:flex-row md:items-end">
        <TextField
          className="md:max-w-sm md:flex-1"
          label={t('companies.search')}
          leadingIcon={Search}
          type="search"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />
        <SegmentedControl<StatusFilter>
          label={t('companies.statusFilter')}
          value={status}
          onValueChange={setStatus}
          options={[
            { value: 'ALL', label: t('common.all') },
            { value: 'ACTIVE', label: t('enums.companyStatus.ACTIVE') },
            { value: 'FROZEN', label: t('enums.companyStatus.FROZEN') },
          ]}
        />
      </div>
      <ListPanel>
        <PagedList
          resource={companies}
          empty={
            filtered ? (
              <EmptyList title={t('companies.noResults')} description={t('companies.noResultsHint')} />
            ) : (
              <EmptyList
                title={t('companies.empty')}
                description={t('companies.emptyHint')}
                action={
                  <Button leadingIcon={Plus} onClick={() => setCreating(true)}>
                    {t('companies.new')}
                  </Button>
                }
              />
            )
          }
        >
          {(items) => (
            <DataView
              caption={t('companies.title')}
              rows={items}
              columns={columns}
              getRowId={(company) => company.id}
              onRowClick={open}
              tableFrom="md"
              renderListItem={(company) => (
                <ListRow
                  leading={
                    <span className="flex size-9 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
                      <Icon icon={Building2} size={18} />
                    </span>
                  }
                  title={name(company)}
                  subtitle={company.taxId ? <span dir="ltr">{company.taxId}</span> : company.legalName}
                  trailing={<CompanyStatusChip status={company.status} />}
                  onClick={() => open(company)}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
      <CompanyFormSheet open={creating} onOpenChange={setCreating} onSaved={open} />
    </>
  );
}
