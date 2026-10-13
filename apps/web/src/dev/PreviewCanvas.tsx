import { ChevronRight, Ellipsis, Plus, Search } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { periodKey } from '@/domain/calendar';
import { amountForVolume, parseDecimal, volumeForAmount } from '@/domain/money';
import type { FuelTransaction } from '@/domain/types';
import {
  AppearanceSettings,
  Banner,
  Button,
  ChartFrame,
  ChartTooltip,
  ConnectivityChip,
  DataView,
  DateTimeText,
  EmptyState,
  ErrorState,
  IconButton,
  KpiTile,
  ListRow,
  Money,
  Numpad,
  Panel,
  Plate,
  SegmentedControl,
  Sheet,
  Skeleton,
  StatusChip,
  Stepper,
  Tabs,
  TextField,
  TierChip,
  toast,
  TransactionStatusChip,
  Volume,
  ZoodWordmark,
  type DataColumn,
} from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { DEFAULT_TIME_ZONE, formatDate, formatMoney, formatNumber, piastresToWholePounds } from '@/lib/format';
import { localize } from '@/lib/locale';
import { DEMO_IDS } from '@/mocks/seed';
import { useDemoStore } from '@/mocks/store';
import { CANVAS_HEIGHT_MESSAGE } from './messages';

const COLOR_TOKENS = [
  { name: 'background', className: 'bg-background' },
  { name: 'surface', className: 'bg-surface' },
  { name: 'surface-elevated', className: 'bg-surface-elevated' },
  { name: 'surface-sunken', className: 'bg-surface-sunken' },
  { name: 'foreground', className: 'bg-foreground' },
  { name: 'muted', className: 'bg-muted' },
  { name: 'border', className: 'bg-border' },
  { name: 'primary', className: 'bg-primary' },
  { name: 'primary-subtle', className: 'bg-primary-subtle' },
  { name: 'accent', className: 'bg-accent' },
  { name: 'success', className: 'bg-success' },
  { name: 'warning', className: 'bg-warning' },
  { name: 'danger', className: 'bg-danger' },
  { name: 'info', className: 'bg-info' },
  { name: 'sidebar', className: 'bg-sidebar' },
  { name: 'focus-ring', className: 'bg-focus-ring' },
] as const;

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-sm font-semibold tracking-wide text-muted uppercase">{title}</h2>
      {children}
    </section>
  );
}

/** Reports the content height to the preview frame so it can size the iframe. */
function useReportHeight() {
  useEffect(() => {
    if (window.parent === window) return;
    const report = () =>
      window.parent.postMessage(
        { type: CANVAS_HEIGHT_MESSAGE, height: document.documentElement.scrollHeight },
        window.location.origin,
      );
    const observer = new ResizeObserver(report);
    observer.observe(document.body);
    report();
    return () => observer.disconnect();
  }, []);
}

export function PreviewCanvas() {
  useReportHeight();
  const { t } = useTranslation(['dev', 'common', 'status']);
  const { locale, dir } = useLocale();
  const data = useDemoStore((state) => state.data);
  const [selected, setSelected] = useState<FuelTransaction | null>(null);
  const [step, setStep] = useState(1);
  const [entryMode, setEntryMode] = useState<'liters' | 'amount'>('liters');
  const [entry, setEntry] = useState('40');
  // One reference time per mount keeps rendering pure (no clock reads during render).
  const [now] = useState(() => new Date());

  const wallet = data.wallets[DEMO_IDS.company];
  const station = data.stations[DEMO_IDS.station];
  const hero = data.vehicles[DEMO_IDS.vehicleCorolla];
  const unitPrice = station?.prices.gas92 ?? 0;

  const companyTransactions = useMemo(
    () => data.transactions.filter((txn) => txn.companyId === DEMO_IDS.company),
    [data.transactions],
  );
  const recent = useMemo(() => companyTransactions.slice(-6).reverse(), [companyTransactions]);

  const today = periodKey(now, 'day', DEFAULT_TIME_ZONE);
  const todays = companyTransactions.filter(
    (txn) => txn.status === 'completed' && periodKey(new Date(txn.deviceAt), 'day', DEFAULT_TIME_ZONE) === today,
  );
  const todaySpend = todays.filter((txn) => txn.billable).reduce((sum, txn) => sum + txn.amountPiastres, 0);
  const fleet = Object.values(data.vehicles).filter((vehicle) => vehicle.companyId === DEMO_IDS.company && vehicle.status === 'active');

  const chartData = useMemo(() => {
    const days = Array.from({ length: 7 }, (_, index) => {
      const at = new Date(now.getTime() - (6 - index) * 86_400_000);
      return { key: periodKey(at, 'day', DEFAULT_TIME_ZONE), at, spend: 0 };
    });
    for (const txn of companyTransactions) {
      if (txn.status !== 'completed' || !txn.billable) continue;
      const day = days.find((item) => item.key === periodKey(new Date(txn.deviceAt), 'day', DEFAULT_TIME_ZONE));
      if (day) day.spend += txn.amountPiastres;
    }
    return days.map((day) => ({
      tick: formatDate(day.at, locale, { month: 'short' }),
      label: formatDate(day.at, locale, { month: 'long' }),
      spend: piastresToWholePounds(day.spend),
    }));
  }, [companyTransactions, locale, now]);

  const stationName = (txn: FuelTransaction) => localize(data.stations[txn.stationId]?.name, locale);
  const fuelName = (txn: FuelTransaction) => localize(data.fuelTypes[txn.fuelTypeId]?.name, locale);
  const plateOf = (txn: FuelTransaction) => data.vehicles[txn.vehicleId]?.plate ?? '';

  const columns: DataColumn<FuelTransaction>[] = [
    { id: 'id', header: t('table.transaction'), cell: (txn) => <span className="font-medium tabular-nums">{txn.shortId}</span> },
    { id: 'vehicle', header: t('table.vehicle'), cell: (txn) => <Plate value={plateOf(txn)} size="sm" /> },
    { id: 'station', header: t('table.station'), cell: stationName },
    { id: 'fuel', header: t('table.fuel'), cell: fuelName },
    { id: 'volume', header: t('table.volume'), align: 'end', cell: (txn) => <Volume value={txn.volumeMl} /> },
    { id: 'amount', header: t('table.amount'), align: 'end', cell: (txn) => <Money value={txn.amountPiastres} /> },
    { id: 'status', header: t('table.status'), cell: (txn) => <TransactionStatusChip status={txn.status} /> },
    { id: 'time', header: t('table.time'), cell: (txn) => <DateTimeText value={txn.deviceAt} className="text-muted" /> },
  ];

  // POS fuel entry: the other unit is derived from the station price with integer math.
  const entered = parseDecimal(entry || '0', entryMode === 'liters' ? 3 : 2) ?? 0;
  const posVolume = entryMode === 'liters' ? entered : volumeForAmount(entered, unitPrice || 1);
  const posAmount = entryMode === 'liters' ? amountForVolume(entered, unitPrice) : entered;
  const appendDigit = (digit: string) => setEntry((value) => (value.length >= 7 ? value : value === '0' ? digit : value + digit));
  const appendDecimal = () => setEntry((value) => (value.includes('.') ? value : `${value || '0'}.`));
  const backspace = () => setEntry((value) => value.slice(0, -1));

  return (
    <div className="mx-auto flex max-w-5xl flex-col gap-10 px-4 py-6 md:px-6">
      <Section title={t('sections.brand')}>
        <div className="flex flex-wrap items-center gap-6">
          <ZoodWordmark size="lg" />
          <ZoodWordmark size="md" />
          <div className="rounded-container bg-sidebar px-5 py-4">
            <ZoodWordmark size="md" tone="inverse" />
          </div>
          <p className="text-muted">{t('common:tagline')}</p>
        </div>
      </Section>

      <Section title={t('sections.colors')}>
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
          {COLOR_TOKENS.map((token) => (
            <div key={token.name} className="flex items-center gap-2 rounded-control border border-border bg-surface p-2">
              <span className={`size-8 shrink-0 rounded-md border border-border ${token.className}`} />
              <code dir="ltr" className="truncate text-xs">
                {token.name}
              </code>
            </div>
          ))}
        </div>
      </Section>

      <Section title={t('sections.typography')}>
        <div className="flex flex-col gap-2">
          <p className="text-xl font-semibold md:text-2xl">{t('typography.pageTitle')}</p>
          <p className="text-base font-semibold">{t('typography.sectionTitle')}</p>
          <p className="max-w-prose">{t('typography.body')}</p>
          <p className="text-xs text-muted">{t('typography.caption')}</p>
        </div>
      </Section>

      <Section title={t('sections.buttons')}>
        <div className="flex flex-wrap items-center gap-2">
          <Button>{t('buttons.primary')}</Button>
          <Button variant="secondary">{t('buttons.secondary')}</Button>
          <Button variant="ghost" trailingIcon={ChevronRight} trailingIconDirectional>
            {t('buttons.ghost')}
          </Button>
          <Button variant="danger">{t('buttons.danger')}</Button>
          <Button leadingIcon={Plus} size="lg">
            {t('buttons.next')}
          </Button>
          <Button loading>{t('buttons.loading')}</Button>
          <Button size="sm" variant="secondary">
            {t('buttons.secondary')}
          </Button>
          <IconButton icon={Ellipsis} label={t('buttons.iconLabel')} variant="secondary" />
        </div>
      </Section>

      <Section title={t('sections.status')}>
        <div className="flex flex-wrap items-center gap-2">
          <TransactionStatusChip status="completed" />
          <TransactionStatusChip status="declined" />
          <TransactionStatusChip status="pendingSync" />
          <TransactionStatusChip status="reversed" />
          <StatusChip tone="warning" label={t('status:review.needsReview')} />
          <TierChip tier="normal" />
          <TierChip tier="vip" />
          <ConnectivityChip online />
          <ConnectivityChip online={false} />
          <StatusChip tone="info" size="md" label={t('status:wallet.available')} />
        </div>
      </Section>

      <Section title={t('sections.values')}>
        <Panel>
          <p className="leading-8">
            <Trans
              t={t}
              i18nKey="values.sentence"
              values={{ station: localize(station?.name, locale) }}
              components={{
                plate: <Plate value={hero?.plate ?? ''} size="sm" />,
                volume: <Volume value={40_000} className="font-semibold" />,
                amount: <Money value={amountForVolume(40_000, unitPrice)} className="font-semibold" />,
              }}
            />
          </p>
          <div className="mt-4 grid gap-3 text-sm sm:grid-cols-3">
            <div className="flex justify-between gap-2 sm:flex-col">
              <span className="text-muted">{t('kpis.walletBalance')}</span>
              <Money value={wallet?.balancePiastres ?? 0} className="font-semibold" />
            </div>
            <div className="flex justify-between gap-2 sm:flex-col">
              <span className="text-muted">{t('values.reversal')}</span>
              <Money value={-76_000} className="font-semibold text-danger" />
            </div>
            <div className="flex justify-between gap-2 sm:flex-col">
              <span className="text-muted">{t('values.topup')}</span>
              <Money value={50_000_000} signDisplay="always" className="font-semibold text-success" />
            </div>
          </div>
          <div className="mt-4 flex flex-wrap items-center gap-3">
            <span className="text-sm text-muted">{t('values.plateSizes')}</span>
            <Plate value={hero?.plate ?? ''} size="sm" />
            <Plate value={hero?.plate ?? ''} size="md" />
            <Plate value={hero?.plate ?? ''} size="lg" />
            <DateTimeText value={now.toISOString()} className="text-sm text-muted" />
          </div>
        </Panel>
      </Section>

      <Section title={t('sections.kpis')}>
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
          <KpiTile
            emphasis
            label={t('kpis.walletBalance')}
            value={<Money value={wallet?.balancePiastres ?? 0} precision="whole" />}
            hint={t('kpis.walletHint', { days: 18 })}
          />
          <KpiTile label={t('kpis.todaySpend')} value={<Money value={todaySpend} precision="whole" />} />
          <KpiTile
            label={t('kpis.todayTransactions')}
            value={formatNumber(todays.length, locale)}
            hint={t('common:counts.transactions', { count: todays.length })}
          />
          <KpiTile
            label={t('kpis.activeVehicles')}
            value={formatNumber(fleet.length, locale)}
            hint={t('kpis.activeVehiclesHint', {
              normal: fleet.filter((vehicle) => vehicle.tier === 'normal').length,
              vip: fleet.filter((vehicle) => vehicle.tier === 'vip').length,
            })}
          />
        </div>
      </Section>

      <Section title={t('sections.lists')}>
        <Panel
          flush
          title={t('table.caption')}
          action={
            <Button variant="ghost" size="sm" trailingIcon={ChevronRight} trailingIconDirectional>
              {t('common:actions.viewAll')}
            </Button>
          }
        >
          <DataView
            caption={t('table.caption')}
            rows={recent}
            columns={columns}
            getRowId={(txn) => txn.id}
            onRowClick={setSelected}
            renderListItem={(txn) => (
              <ListRow
                onClick={() => setSelected(txn)}
                title={<Plate value={plateOf(txn)} variant="plain" />}
                subtitle={
                  <>
                    {stationName(txn)} · <DateTimeText value={txn.deviceAt} variant="time" />
                  </>
                }
                trailing={<Money value={txn.amountPiastres} />}
                trailingSub={<TransactionStatusChip status={txn.status} />}
              />
            )}
          />
        </Panel>
      </Section>

      <Section title={t('sections.overlays')}>
        <div>
          <Button variant="secondary" onClick={() => setSelected(recent[0] ?? null)}>
            {t('sheet.open')}
          </Button>
        </div>
        <Sheet
          open={selected !== null}
          onOpenChange={(open) => !open && setSelected(null)}
          title={t('sheet.title')}
          description={t('sheet.description')}
          footer={
            <Button variant="secondary" onClick={() => setSelected(null)}>
              {t('common:actions.close')}
            </Button>
          }
        >
          {selected && (
            <div className="flex flex-col gap-4">
              <div className="flex flex-col items-start gap-2">
                <TransactionStatusChip status={selected.status} size="md" />
                <Money value={selected.amountPiastres} className="text-3xl font-semibold" />
                <Plate value={plateOf(selected)} size="md" />
              </div>
              <dl className="divide-y divide-border rounded-container border border-border">
                {[
                  [t('sheet.station'), stationName(selected)],
                  [t('sheet.fuel'), fuelName(selected)],
                  [t('sheet.volume'), <Volume key="volume" value={selected.volumeMl} />],
                  [t('sheet.time'), <DateTimeText key="time" value={selected.deviceAt} />],
                ].map(([label, value]) => (
                  <div key={String(label)} className="flex items-center justify-between gap-3 px-4 py-3 text-sm">
                    <dt className="text-muted">{label}</dt>
                    <dd className="font-medium">{value}</dd>
                  </div>
                ))}
              </dl>
            </div>
          )}
        </Sheet>
      </Section>

      <Section title={t('sections.navigation')}>
        <Panel>
          <Tabs
            label={t('sections.navigation')}
            items={[
              { value: 'movements', label: t('tabs.movements'), content: <p className="text-sm text-muted">{t('tabs.movementsBody')}</p> },
              { value: 'topups', label: t('tabs.topups'), content: <p className="text-sm text-muted">{t('tabs.topupsBody')}</p> },
            ]}
          />
          <div className="mt-6 flex flex-col gap-4">
            <Stepper
              current={step}
              steps={[t('steps.vehicle'), t('steps.operations'), t('steps.tag'), t('steps.limits'), t('steps.review')]}
            />
            <div className="flex gap-2">
              <Button variant="secondary" disabled={step === 0} onClick={() => setStep((value) => value - 1)}>
                {t('common:actions.back')}
              </Button>
              <Button
                disabled={step === 4}
                trailingIcon={ChevronRight}
                trailingIconDirectional
                onClick={() => setStep((value) => value + 1)}
              >
                {t('common:actions.next')}
              </Button>
            </div>
          </div>
        </Panel>
      </Section>

      <Section title={t('sections.feedback')}>
        <Banner
          tone="warning"
          title={t('feedback.bannerTitle')}
          description={t('feedback.bannerBody')}
          action={
            <Button size="sm" variant="secondary">
              {t('feedback.bannerAction')}
            </Button>
          }
        />
        <Banner tone="info" title={t('feedback.offline')} />
        <div className="grid gap-3 md:grid-cols-2">
          <Panel>
            <EmptyState />
          </Panel>
          <Panel>
            <ErrorState onRetry={() => toast(t('feedback.toastMessage'), 'info')} />
          </Panel>
        </div>
        <div className="flex flex-col gap-2" aria-label={t('feedback.skeleton')}>
          <Skeleton className="h-5 w-2/3" />
          <Skeleton className="h-5 w-1/2" />
          <Skeleton className="h-16" />
        </div>
        <div>
          <Button variant="secondary" onClick={() => toast(t('feedback.toastMessage'))}>
            {t('feedback.showToast')}
          </Button>
        </div>
      </Section>

      <Section title={t('sections.forms')}>
        <Panel>
          <div className="grid gap-4 md:grid-cols-2">
            <TextField label={t('forms.plate')} hint={t('forms.plateHint')} defaultValue={hero?.plate} dir="rtl" lang="ar" />
            <TextField label={t('forms.amount')} inputMode="decimal" defaultValue="0" error={t('forms.amountError')} />
            <TextField label={t('common:actions.search')} placeholder={t('forms.search')} leadingIcon={Search} className="md:col-span-2" />
          </div>
        </Panel>
      </Section>

      <Section title={t('sections.pos')}>
        <div data-surface="pos" className="mx-auto flex w-full max-w-sm flex-col gap-4 rounded-container border border-border bg-surface p-4">
          <div className="flex items-center justify-between gap-2">
            <Plate value={hero?.plate ?? ''} size="md" />
            <ConnectivityChip online={false} />
          </div>
          <SegmentedControl
            label={t('pos.entryMode')}
            value={entryMode}
            onValueChange={(mode) => {
              setEntryMode(mode);
              setEntry('');
            }}
            options={[
              { value: 'liters', label: t('pos.liters') },
              { value: 'amount', label: t('pos.amount') },
            ]}
            className="w-full"
          />
          <div className="flex flex-col items-center gap-1 py-2 text-center">
            {entryMode === 'liters' ? (
              <Volume value={posVolume} className="text-(length:--pos-value-size) leading-none font-semibold" />
            ) : (
              <Money value={posAmount} className="text-(length:--pos-value-size) leading-none font-semibold" />
            )}
            <span className="text-lg text-muted">
              {entryMode === 'liters' ? <Money value={posAmount} /> : <Volume value={posVolume} />}
            </span>
            <span className="text-xs text-muted">
              {t('pos.pricePerLiter', { price: formatMoney(unitPrice, locale) })}
            </span>
          </div>
          <Numpad onDigit={appendDigit} onDecimal={appendDecimal} onBackspace={backspace} />
          <Button size="pos" fullWidth disabled={posVolume <= 0}>
            {t('pos.review')}
          </Button>
          <Banner tone="warning" title={t('feedback.offline')} />
        </div>
      </Section>

      <Section title={t('sections.charts')}>
        <ChartFrame
          title={t('chart.spendTitle')}
          description={t('chart.note')}
          legend={[{ label: t('chart.spend'), series: 0 }]}
        >
          {(theme) => (
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={chartData} margin={{ top: 4, right: 4, bottom: 0, left: 4 }}>
                <CartesianGrid vertical={false} stroke={theme.grid} />
                <XAxis
                  dataKey="tick"
                  tickLine={false}
                  axisLine={{ stroke: theme.grid }}
                  tick={{ fill: theme.axis, fontSize: 11 }}
                  interval="preserveStartEnd"
                />
                <YAxis
                  width={56}
                  tickLine={false}
                  axisLine={false}
                  tick={{ fill: theme.axis, fontSize: 11 }}
                  tickFormatter={(value: number) => formatNumber(value, locale)}
                />
                <Tooltip
                  cursor={{ fill: theme.grid, opacity: 0.5 }}
                  content={({ active, payload }) => {
                    const point = payload?.[0]?.payload as (typeof chartData)[number] | undefined;
                    return (
                      <ChartTooltip
                        active={active}
                        dir={dir}
                        theme={theme}
                        label={point?.label}
                        rows={point ? [{ label: t('chart.spend'), value: formatMoney(point.spend * 100, locale, { precision: 'whole' }), series: 0 }] : []}
                      />
                    );
                  }}
                />
                <Bar dataKey="spend" fill={theme.series[0]} radius={[4, 4, 0, 0]} maxBarSize={36} />
              </BarChart>
            </ResponsiveContainer>
          )}
        </ChartFrame>
      </Section>

      <Section title={t('sections.settings')}>
        <AppearanceSettings />
      </Section>
    </div>
  );
}
