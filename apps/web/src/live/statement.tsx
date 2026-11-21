/**
 * The wallet statement with a date filter and a CSV export (QA round 1: "the statement cannot be
 * filtered or exported"). The API's statement is newest first without a date filter, so a range is
 * read page by page until it passes the start day.
 */
import { Download } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, FilterBar } from '@/design-system';
import { periodRange } from '@/lib/periods';
import { ListPanel, PageSection } from '@/shell/page';
import type { StatementEntry } from './api';
import { DateRangeFields } from './date-range';
import { useDateRange, usableRange } from './date-range-state';
import { statementInRange } from './insights/model';
import { useStatementExport, type LoadStatement } from './statement-export';
import { PartialNote } from './insights/ui';
import { usePagedResource, useResource, type PagedResource } from './use-resource';
import { StatementList } from './wallet';

/** The statement section of a wallet page: a period, the movements in it, and the export. */
export function StatementSection({ load, className }: { load: LoadStatement; className?: string }) {
  const { t } = useTranslation(['live', 'portal']);
  const range = useDateRange('CUSTOM');
  const { from, to, valid } = usableRange(range.from, range.to);
  const filtered = Boolean(from || to);
  const paged = usePagedResource((page, signal) => (filtered ? Promise.resolve({ items: [] }) : load(page, signal)), [filtered]);
  const ranged = useResource((signal) => (filtered ? statementInRange(load, { from, to }, { signal }) : Promise.resolve(undefined)), [from, to]);
  const exporter = useStatementExport(load);

  const resource: PagedResource<StatementEntry> = filtered
    ? {
        items: ranged.data?.items ?? [],
        error: ranged.error,
        loading: ranged.loading,
        loadingMore: false,
        hasMore: false,
        loadMore: () => undefined,
        reload: ranged.reload,
      }
    : paged;

  const exportNow = () => {
    // Without a period the export covers this month so far.
    const month = periodRange('THIS_MONTH');
    void exporter.run(filtered ? { from: from || month.from, to: to || month.to } : month);
  };

  return (
    <PageSection
      className={className}
      title={t('wallet.statement')}
      description={t('wallet.statementHint')}
      action={
        <Button variant="secondary" size="sm" leadingIcon={Download} loading={exporter.busy} disabled={!valid} onClick={exportNow}>
          {t('portal:statement.export')}
        </Button>
      }
    >
      <ListPanel>
        <FilterBar
          activeCount={range.from || range.to ? 1 : 0}
          onReset={() => {
            range.setFrom('');
            range.setTo('');
          }}
        >
          <DateRangeFields range={range} />
        </FilterBar>
        <StatementList resource={resource} />
        {filtered && ranged.data?.truncated && (
          <div className="border-t border-border px-4 py-3">
            <PartialNote count={ranged.data.items.length} />
          </div>
        )}
      </ListPanel>
    </PageSection>
  );
}
