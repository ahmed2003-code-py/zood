import { Download, FileUp, Upload } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { apiDownload } from '@/api/http-client';
import { Banner, Button, DataView, DateTimeText, ErrorState, FileField, KpiTile, ListRow, SegmentedControl, Skeleton, toast, type DataColumn } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { BackLink, ListPanel, PageHeader, PageSection } from '@/shell/page';
import { opsApi, type FleetImport, type FleetImportMode, type FleetImportRow } from '../api';
import { ImportRowStatusChip, ImportStatusChip } from '../chips';
import { EmptyList, PagedList } from '../components';
import { liveError } from '../errors';
import { usePagedResource, useResource } from '../use-resource';

const MAX_BYTES = 5 * 1024 * 1024;

const COLUMN_KEYS = [
  'plate',
  'fleet_code',
  'make',
  'model',
  'year',
  'fuel_type',
  'tank_capacity_l',
  'department',
  'tier',
  'vip_monthly_ceiling_l',
  'daily_limit_l',
  'weekly_limit_l',
  'monthly_limit_l',
] as const;
type ColumnKey = (typeof COLUMN_KEYS)[number];
const isColumnKey = (key: string): key is ColumnKey => (COLUMN_KEYS as readonly string[]).includes(key);

const ROW_ERROR_CODES = [
  'REQUIRED',
  'TOO_LONG',
  'INVALID_PLATE',
  'INVALID_YEAR',
  'INVALID_FUEL_TYPE',
  'INVALID_TANK_CAPACITY',
  'UNKNOWN_DEPARTMENT',
  'INVALID_TIER',
  'INVALID_NUMBER',
  'VIP_CEILING_ONLY_FOR_VIP',
  'INVALID_LIMIT',
  'DUPLICATE_PLATE_IN_FILE',
  'DUPLICATE_FLEET_CODE_IN_FILE',
  'PLATE_IN_USE',
  'FLEET_CODE_IN_USE',
  'INVALID_FLEET_CODE',
  'INTERNAL',
] as const;
type RowErrorCode = (typeof ROW_ERROR_CODES)[number];
const isRowErrorCode = (code: string): code is RowErrorCode => (ROW_ERROR_CODES as readonly string[]).includes(code);

type Format = 'csv' | 'xlsx';
type Lang = 'ar' | 'en';

/**
 * Ops: add a company's vehicles in bulk from a CSV or XLSX file. The file is checked first and
 * nothing is created until it is confirmed; the import then runs on the server and is followed here.
 */
export function ImportPage() {
  const { id = '' } = useParams();
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const [importId, setImportId] = useState<string>();
  const company = useResource((signal) => opsApi.companies.get(id, signal), [id]);
  const name = company.data ? (locale === 'ar' ? company.data.nameAr : company.data.nameEn) : undefined;

  return (
    <>
      <BackLink to={`/ops/companies/${id}`} label={name ?? t('company.back')} />
      <PageHeader title={t('import.title')} description={t('import.description')} />
      {importId ? (
        <ImportJob key={importId} companyId={id} importId={importId} onLeave={() => setImportId(undefined)} />
      ) : (
        <div className="flex flex-col gap-8">
          <div className="grid grid-cols-1 gap-8 lg:grid-cols-2">
            <TemplateSection />
            <UploadSection companyId={id} onUploaded={setImportId} />
          </div>
          <PastImports companyId={id} onOpen={setImportId} />
        </div>
      )}
    </>
  );
}

function TemplateSection() {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const [format, setFormat] = useState<Format>('xlsx');
  const [lang, setLang] = useState<Lang>(locale === 'ar' ? 'ar' : 'en');
  const [busy, setBusy] = useState(false);
  const download = async () => {
    setBusy(true);
    try {
      await apiDownload(`/ops/fleet-imports/template?format=${format}&lang=${lang}`, `vehicles-template-${lang}.${format}`);
    } catch (error) {
      toast(liveError(t, error).message, 'danger');
    } finally {
      setBusy(false);
    }
  };
  return (
    <PageSection title={t('import.templateTitle')}>
      <div className="flex flex-col gap-4 rounded-container border border-border bg-surface p-4 shadow-card md:p-5">
        <p className="text-sm text-muted">{t('import.templateHint')}</p>
        <div className="flex flex-wrap gap-4">
          <SegmentedControl<Format>
            label={t('import.format')}
            value={format}
            onValueChange={setFormat}
            options={[
              { value: 'xlsx', label: t('import.formats.xlsx') },
              { value: 'csv', label: t('import.formats.csv') },
            ]}
          />
          <SegmentedControl<Lang>
            label={t('import.language')}
            value={lang}
            onValueChange={setLang}
            options={[
              { value: 'ar', label: t('import.languages.ar') },
              { value: 'en', label: t('import.languages.en') },
            ]}
          />
        </div>
        <div>
          <Button variant="secondary" leadingIcon={Download} loading={busy} onClick={download}>
            {t('import.downloadTemplate')}
          </Button>
        </div>
      </div>
    </PageSection>
  );
}

function UploadSection({ companyId, onUploaded }: { companyId: string; onUploaded: (id: string) => void }) {
  const { t } = useTranslation('live');
  const [file, setFile] = useState<File>();
  const [problem, setProblem] = useState<string>();
  const [busy, setBusy] = useState(false);

  const choose = (chosen: File | undefined) => {
    setFile(undefined);
    setProblem(undefined);
    if (!chosen) return;
    if (!/\.(csv|xlsx)$/i.test(chosen.name)) return setProblem(t('errors.IMPORT_FILE_TYPE'));
    if (chosen.size === 0) return setProblem(t('errors.IMPORT_FILE_EMPTY'));
    if (chosen.size > MAX_BYTES) return setProblem(t('errors.IMPORT_FILE_TOO_LARGE'));
    setFile(chosen);
  };

  const upload = async () => {
    if (!file || busy) return;
    setBusy(true);
    setProblem(undefined);
    try {
      const created = await opsApi.fleetImports.upload(companyId, file);
      onUploaded(created.id);
    } catch (error) {
      setProblem(liveError(t, error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <PageSection title={t('import.uploadTitle')}>
      <div className="flex flex-col gap-4 rounded-container border border-border bg-surface p-4 shadow-card md:p-5">
        <FileField label={t('import.file')} hint={t('import.fileHint')} accept=".csv,.xlsx" file={file} onFileChange={choose} />
        {problem && <Banner tone="danger" title={problem} />}
        <div>
          <Button leadingIcon={Upload} disabled={!file} loading={busy} onClick={upload}>
            {t('import.upload')}
          </Button>
        </div>
      </div>
    </PageSection>
  );
}

function PastImports({ companyId, onOpen }: { companyId: string; onOpen: (id: string) => void }) {
  const { t } = useTranslation('live');
  const imports = usePagedResource((page, signal) => opsApi.fleetImports.list({ ...page, companyId }, signal), [companyId], 10);
  return (
    <PageSection title={t('import.pastTitle')}>
      <ListPanel>
        <PagedList resource={imports} empty={<EmptyList title={t('import.pastEmpty')} description={t('import.pastEmptyHint')} />}>
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((item) => (
                <ListRow
                  key={item.id}
                  title={<span dir="ltr">{item.fileName}</span>}
                  subtitle={
                    <>
                      <DateTimeText value={item.createdAt} /> {t('import.pastCounts', { rows: item.rowCount, imported: item.importedCount })}
                    </>
                  }
                  trailing={<ImportStatusChip status={item.status} />}
                  onClick={() => onOpen(item.id)}
                />
              ))}
            </div>
          )}
        </PagedList>
      </ListPanel>
    </PageSection>
  );
}

/** One import, followed from the preview through the run to the result. Polls while the server works. */
function ImportJob({ companyId, importId, onLeave }: { companyId: string; importId: string; onLeave: () => void }) {
  const { t } = useTranslation('live');
  const job = useResource((signal) => opsApi.fleetImports.get(importId, signal), [importId]);
  const data = job.data;
  const working = data?.status === 'QUEUED' || data?.status === 'RUNNING';
  const reload = job.reload;

  useEffect(() => {
    if (!working) return;
    const timer = window.setInterval(reload, 2000);
    return () => window.clearInterval(timer);
  }, [working, reload]);

  if (job.error && !data) return <ErrorState onRetry={job.reload} />;
  if (!data) {
    return (
      <div className="flex flex-col gap-4" aria-busy>
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-32" />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex min-w-0 items-center gap-3">
          <span dir="ltr" className="truncate font-medium">
            {data.fileName}
          </span>
          <ImportStatusChip status={data.status} />
        </div>
        <Button variant="ghost" onClick={onLeave}>
          {t('import.backToImports')}
        </Button>
      </div>
      {data.status === 'VALIDATED' && <Preview job={data} onChanged={job.reload} onLeave={onLeave} />}
      {working && <Progress job={data} />}
      {(data.status === 'COMPLETED' || data.status === 'COMPLETED_WITH_ERRORS') && <Results job={data} companyId={companyId} onLeave={onLeave} />}
      {data.status === 'CANCELLED' && <Banner tone="info" title={t('import.cancelledTitle')} description={t('import.cancelledHint')} />}
    </div>
  );
}

function Counts({ job }: { job: FleetImport }) {
  const { t } = useTranslation('live');
  return (
    <div className="grid grid-cols-3 gap-3">
      <KpiTile label={t('import.rows')} value={job.rowCount} />
      <KpiTile label={t('import.valid')} value={job.validCount} />
      <KpiTile emphasis={job.invalidCount > 0} label={t('import.withProblems')} value={job.invalidCount} />
    </div>
  );
}

function Preview({ job, onChanged, onLeave }: { job: FleetImport; onChanged: () => void; onLeave: () => void }) {
  const { t } = useTranslation('live');
  const [validOnly, setValidOnly] = useState(false);
  const [busy, setBusy] = useState<'confirm' | 'cancel'>();
  const [failure, setFailure] = useState<string>();
  const hasProblems = job.invalidCount > 0;
  const nothing = job.validCount === 0;
  const mode: FleetImportMode = validOnly ? 'VALID_ROWS_ONLY' : 'STRICT';
  const blocked = nothing || (hasProblems && !validOnly);

  const run = async (kind: 'confirm' | 'cancel') => {
    setBusy(kind);
    setFailure(undefined);
    try {
      if (kind === 'confirm') {
        await opsApi.fleetImports.confirm(job.id, mode);
        onChanged();
      } else {
        await opsApi.fleetImports.cancel(job.id);
        toast(t('import.cancelled'));
        onLeave();
      }
    } catch (error) {
      setFailure(liveError(t, error).message);
    } finally {
      setBusy(undefined);
    }
  };

  const downloadErrors = async () => {
    try {
      await apiDownload(`/ops/fleet-imports/${job.id}/errors.csv`, `import-errors-${job.id}.csv`);
    } catch (error) {
      toast(liveError(t, error).message, 'danger');
    }
  };

  return (
    <>
      {job.sameFileImportedBefore && <Banner tone="warning" title={t('import.sameFileTitle')} description={t('import.sameFileHint')} />}
      <Counts job={job} />
      {hasProblems && (
        <PageSection
          title={t('import.problemsTitle')}
          action={
            <Button variant="secondary" size="sm" leadingIcon={Download} onClick={downloadErrors}>
              {t('import.downloadErrors')}
            </Button>
          }
        >
          <ProblemRows importId={job.id} />
        </PageSection>
      )}
      <PageSection title={t('import.confirmTitle')}>
        <div className="flex flex-col gap-4 rounded-container border border-border bg-surface p-4 shadow-card md:p-5">
          {nothing ? (
            <Banner tone="warning" title={t('import.nothingTitle')} description={t('import.nothingHint')} />
          ) : hasProblems ? (
            <>
              <Banner tone="warning" title={t('import.strictBlockedTitle')} description={t('import.strictBlockedHint')} />
              <label className="flex items-start gap-3 text-sm">
                <input
                  type="checkbox"
                  checked={validOnly}
                  onChange={(event) => setValidOnly(event.target.checked)}
                  className="mt-0.5 size-5 shrink-0 accent-primary"
                />
                <span>{t('import.validOnly', { valid: job.validCount })}</span>
              </label>
            </>
          ) : (
            <p className="text-sm text-muted">{t('import.allValid', { valid: job.validCount })}</p>
          )}
          {failure && <Banner tone="danger" title={failure} />}
          <div className="flex flex-wrap gap-2">
            <Button leadingIcon={FileUp} disabled={blocked} loading={busy === 'confirm'} onClick={() => run('confirm')}>
              {t(validOnly ? 'import.confirmValidOnly' : 'import.confirm', { valid: job.validCount })}
            </Button>
            <Button variant="secondary" loading={busy === 'cancel'} onClick={() => run('cancel')}>
              {t('import.cancel')}
            </Button>
          </div>
        </div>
      </PageSection>
    </>
  );
}

/** The rows with a problem, 100 at a time (the API pages by row number). */
function ProblemRows({ importId }: { importId: string }) {
  const { t } = useTranslation('live');
  const first = useResource((signal) => opsApi.fleetImports.rows(importId, { problemsOnly: true, limit: 100 }, signal), [importId]);
  const [more, setMore] = useState<{ items: FleetImportRow[]; next?: number; started: boolean; busy: boolean; failed: boolean }>({
    items: [],
    started: false,
    busy: false,
    failed: false,
  });

  if (first.error && !first.data) return <ErrorState onRetry={first.reload} />;
  if (!first.data) {
    return (
      <div className="flex flex-col gap-3 p-4" aria-busy>
        {Array.from({ length: 3 }, (_, index) => (
          <Skeleton key={index} className="h-10" />
        ))}
      </div>
    );
  }

  const rows = [...first.data.items, ...more.items];
  const nextAfter = more.started ? more.next : (first.data.nextAfter ?? undefined);

  const loadMore = async () => {
    if (nextAfter === undefined) return;
    setMore((previous) => ({ ...previous, busy: true, failed: false }));
    try {
      const page = await opsApi.fleetImports.rows(importId, { problemsOnly: true, limit: 100, after: nextAfter });
      setMore((previous) => ({
        items: [...previous.items, ...page.items],
        next: page.nextAfter ?? undefined,
        started: true,
        busy: false,
        failed: false,
      }));
    } catch {
      setMore((previous) => ({ ...previous, busy: false, failed: true }));
    }
  };

  const problems = (row: FleetImportRow) => (
    <ul className="flex flex-col gap-0.5">
      {row.errors.map((error) => (
        <li key={`${error.column}-${error.code}`} className="text-sm">
          <span className="font-medium">{isColumnKey(error.column) ? t(`import.columns.${error.column}`) : error.column}</span>{' · '}
          <span className="text-muted">{isRowErrorCode(error.code) ? t(`import.rowErrors.${error.code}`) : error.code}</span>
        </li>
      ))}
    </ul>
  );
  const columns: DataColumn<FleetImportRow>[] = [
    { id: 'row', header: t('import.rowNumber'), align: 'end', cell: (row) => <span className="tabular-nums">{row.rowNumber}</span> },
    { id: 'plate', header: t('vehicleForm.plate'), cell: (row) => <span dir="ltr">{row.plate ?? t('common.notSet')}</span> },
    { id: 'status', header: t('vehicle.status'), cell: (row) => <ImportRowStatusChip status={row.status} /> },
    { id: 'problems', header: t('import.problems'), cell: problems },
  ];

  return (
    <ListPanel>
      <DataView
        caption={t('import.problemsTitle')}
        rows={rows}
        columns={columns}
        getRowId={(row) => String(row.rowNumber)}
        tableFrom="md"
        renderListItem={(row) => (
          <div className="flex flex-col gap-1 px-4 py-3">
            <div className="flex items-center justify-between gap-3">
              <p className="font-medium">
                <span className="tabular-nums">{row.rowNumber}</span> <span dir="ltr">{row.plate ?? t('common.notSet')}</span>
              </p>
              <ImportRowStatusChip status={row.status} />
            </div>
            {problems(row)}
          </div>
        )}
      />
      {(nextAfter !== undefined || more.failed) && (
        <div className="flex justify-center border-t border-border p-3">
          {more.failed ? (
            <ErrorState onRetry={loadMore} className="py-4" />
          ) : (
            <Button variant="ghost" loading={more.busy} onClick={loadMore}>
              {t('common.loadMore')}
            </Button>
          )}
        </div>
      )}
    </ListPanel>
  );
}

function Progress({ job }: { job: FleetImport }) {
  const { t } = useTranslation('live');
  const done = job.importedCount + job.existingCount + job.failedCount;
  const percent = job.validCount > 0 ? Math.min(100, Math.round((done * 100) / job.validCount)) : 0;
  return (
    <div className="flex flex-col gap-3 rounded-container border border-border bg-surface p-4">
      <p className="font-medium">{t(job.status === 'QUEUED' ? 'import.queued' : 'import.running')}</p>
      <div
        role="progressbar"
        aria-label={t('import.progress')}
        aria-valuemin={0}
        aria-valuemax={job.validCount}
        aria-valuenow={done}
        className="h-2.5 overflow-hidden rounded-full bg-surface-sunken"
      >
        <div className="h-full rounded-full bg-primary transition-[width]" style={{ width: `${percent}%` }} />
      </div>
      <p className="text-sm text-muted tabular-nums">{t('import.progressText', { done, total: job.validCount })}</p>
    </div>
  );
}

function Results({ job, companyId, onLeave }: { job: FleetImport; companyId: string; onLeave: () => void }) {
  const { t } = useTranslation('live');
  const skipped = job.mode === 'VALID_ROWS_ONLY' ? job.invalidCount : 0;
  const clean = job.status === 'COMPLETED';
  return (
    <>
      <Banner tone={clean ? 'success' : 'warning'} title={t(clean ? 'import.doneTitle' : 'import.doneWithErrorsTitle')} description={t('import.doneHint')} />
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <KpiTile emphasis label={t('import.imported')} value={job.importedCount} />
        <KpiTile label={t('import.existing')} value={job.existingCount} hint={t('import.existingHint')} />
        <KpiTile label={t('import.failed')} value={job.failedCount} />
        <KpiTile label={t('import.skipped')} value={skipped} hint={t('import.skippedHint')} />
      </div>
      <div className="flex flex-wrap gap-2">
        <Link
          to={`/ops/companies/${companyId}`}
          className="inline-flex min-h-11 items-center rounded-control bg-primary px-4 font-medium text-primary-foreground hover:bg-primary-hover"
        >
          {t('import.viewVehicles')}
        </Link>
        <Button variant="secondary" onClick={onLeave}>
          {t('import.anotherImport')}
        </Button>
      </div>
    </>
  );
}
