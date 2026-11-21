/** The wallet statement as a CSV file, for any range of Cairo days. */
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatDateTime } from '@/lib/format';
import type { Page, PageQuery, StatementEntry } from './api';
import { downloadCsv, piastresCell, statementInRange, toCsv } from './insights/model';

export type LoadStatement = (page: PageQuery, signal?: AbortSignal) => Promise<Page<StatementEntry>>;

/** Builds the statement CSV for a range and hands it to the browser. */
export function useStatementExport(load: LoadStatement) {
  const { t } = useTranslation(['live', 'portal']);
  const { locale } = useLocale();
  const [busy, setBusy] = useState(false);
  const run = async (range: { from: string; to: string }) => {
    setBusy(true);
    try {
      const { items } = await statementInRange(load, range);
      const rows = [
        [t('portal:statement.csv.date'), t('portal:statement.csv.type'), t('portal:statement.csv.reference'), t('portal:statement.csv.amount'), t('portal:statement.csv.balance')],
        ...items.map((entry) => [
          formatDateTime(entry.postedAt, locale),
          t(`wallet.sources.${entry.sourceType}`),
          entry.reference ?? '',
          piastresCell(entry.direction === 'CREDIT' ? entry.amountPiastres : -entry.amountPiastres),
          piastresCell(entry.balanceAfterPiastres),
        ]),
      ];
      downloadCsv(toCsv(rows), `zawed-statement-${range.from}-${range.to}.csv`);
      toast(t('portal:statement.exported', { count: items.length }));
    } catch {
      toast(t('portal:statement.exportFailed'), 'danger');
    } finally {
      setBusy(false);
    }
  };
  return { busy, run };
}

