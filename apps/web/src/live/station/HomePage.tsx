/**
 * Station portal home: today at the operator's stations (litres and value per station), what ZAWED
 * owes and what is available for the next payout, held fuelings with their reasons, what needs the
 * manager's attention (terminals, agents, tickets) and who is on shift now. Every block loads on its
 * own, so a slow call never blanks the page.
 */
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import {
  ArrowUpRight,
  CircleAlert,
  CircleCheck,
  KeyRound,
  Landmark,
  LifeBuoy,
  Lock,
  MonitorSmartphone,
  PauseCircle,
  Smartphone,
  UserRound,
  WifiOff,
} from 'lucide-react';
import { EmptyState, ErrorState, Icon, KpiTile, Money, Plate, Skeleton, StatusChip, Volume, type LucideIcon, type Tone } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatNumber } from '@/lib/format';
import { cairoDay } from '@/lib/periods';
import { ListPanel, PageHeader, PageSection } from '@/shell/page';
import { stationApi, type Fueling, type StationStatementEntry } from '../api';
import { useResource } from '../use-resource';
import { useStationName } from './hooks';
import { DayText, Stamp } from './parts';
import { agentHealth, deviceHealth } from './station-model';

const HELD_SHOWN = 4;

interface HeldItem {
  entry: StationStatementEntry;
  stationId: string;
  fueling?: Fueling;
}

/** One line of "needs attention": what, how many or which, and where to act. */
interface Attention {
  id: string;
  tone: Tone;
  icon: LucideIcon;
  title: string;
  detail?: string;
  to: string;
}

export function StationHomePage() {
  const { t } = useTranslation(['station', 'live']);
  const { locale } = useLocale();
  const today = cairoDay(new Date());
  const now = useMemo(() => new Date(), []);

  const stations = useResource((signal) => stationApi.stations({ limit: 100 }, signal), []);
  const stationList = stations.data?.items;
  const stationIds = (stationList ?? []).map((station) => station.id);
  const stationName = useStationName(stationList);

  // Today and the payable, per station.
  const perStation = useResource(
    (signal) =>
      Promise.all(
        stationIds.map(async (id) => {
          const [days, payable] = await Promise.all([stationApi.activity({ from: today, to: today, stationId: id }, signal), stationApi.payable(id, signal)]);
          const day = days.find((item) => item.day === today) ?? days[0];
          return { id, day, payable };
        }),
      ),
    [stationIds.join(','), today],
  );
  // Held fuelings: the recent statement lines still held, with the fueling's reasons.
  const held = useResource(
    async (signal): Promise<HeldItem[]> => {
      const pages = await Promise.all(stationIds.map((id) => stationApi.statement(id, { limit: 50 }, signal).then((page) => ({ id, page }))));
      const items = pages.flatMap(({ id, page }) =>
        page.items.filter((entry) => entry.fueling?.payableHeld && !entry.fueling.holdReleased).map((entry) => ({ entry, stationId: id })),
      );
      return Promise.all(
        items.slice(0, HELD_SHOWN).map(async (item) => ({
          ...item,
          fueling: item.entry.fueling ? await stationApi.fueling(item.entry.fueling.id, signal).catch(() => undefined) : undefined,
        })),
      ).then((shown) => [...shown, ...items.slice(HELD_SHOWN)]);
    },
    [stationIds.join(',')],
  );
  const devices = useResource((signal) => stationApi.devices({ limit: 100 }, signal), []);
  const agents = useResource((signal) => stationApi.agents({ limit: 100 }, signal), []);
  const shifts = useResource((signal) => stationApi.shifts({ limit: 50 }, signal), []);
  const payouts = useResource((signal) => stationApi.payouts({ limit: 1 }, signal), []);
  const tickets = useResource((signal) => stationApi.tickets.list({ limit: 50 }, signal), []);

  const totals = useMemo(() => {
    const lines = perStation.data ?? [];
    return {
      amount: lines.reduce((sum, line) => sum + (line.day?.amountPiastres ?? 0), 0),
      volume: lines.reduce((sum, line) => sum + (line.day?.volumeMl ?? 0), 0),
      fuelings: lines.reduce((sum, line) => sum + (line.day?.completed ?? 0), 0),
      declined: lines.reduce((sum, line) => sum + (line.day?.declined ?? 0), 0),
      owed: lines.reduce((sum, line) => sum + line.payable.balancePiastres, 0),
      available: lines.reduce((sum, line) => sum + line.payable.availablePiastres, 0),
      held: lines.reduce((sum, line) => sum + line.payable.heldPiastres, 0),
    };
  }, [perStation.data]);

  const deviceStates = useMemo(() => (devices.data?.items ?? []).map((device) => ({ device, health: deviceHealth(device, now) })), [devices.data, now]);
  const working = deviceStates.filter((item) => item.health === 'ONLINE' || item.health === 'OFFLINE' || item.health === 'NEVER_SYNCED');
  const online = deviceStates.filter((item) => item.health === 'ONLINE').length;
  const onShift = (shifts.data?.items ?? []).filter((shift) => !shift.endedAt);
  const deviceLabel = (id: string) => {
    const device = devices.data?.items.find((item) => item.id === id);
    return device?.label ?? device?.code;
  };

  const attention = useMemo<Attention[]>(() => {
    const list: Attention[] = [];
    for (const { device, health } of deviceStates) {
      const name = device.label ?? device.code;
      if (health === 'CODE_EXPIRED') list.push({ id: device.id, tone: 'danger', icon: CircleAlert, title: t('live.attention.codeExpired', { name }), detail: t('live.attention.codeExpiredHint'), to: '/station/devices' });
      if (health === 'OFFLINE') list.push({ id: device.id, tone: 'warning', icon: WifiOff, title: t('live.attention.offline', { name }), detail: stationName(device.stationId), to: '/station/devices' });
    }
    for (const agent of agents.data?.items ?? []) {
      const health = agentHealth(agent, now);
      if (health === 'LOCKED') list.push({ id: agent.id, tone: 'danger', icon: Lock, title: t('live.attention.locked', { name: agent.fullName }), detail: t('live.attention.lockedHint'), to: '/station/devices' });
      if (health === 'MUST_CHANGE_PIN') list.push({ id: agent.id, tone: 'info', icon: KeyRound, title: t('live.attention.mustChangePin', { name: agent.fullName }), detail: t('live.attention.mustChangePinHint'), to: '/station/devices' });
    }
    const heldCount = held.data?.length ?? 0;
    if (heldCount > 0) list.push({ id: 'held', tone: 'warning', icon: PauseCircle, title: t('live.attention.held', { count: heldCount }), detail: t('live.attention.heldHint'), to: '/station/settlements' });
    const answered = (tickets.data?.items ?? []).filter((ticket) => ticket.status === 'IN_PROGRESS' || ticket.status === 'OPEN');
    if (answered.length > 0) list.push({ id: 'tickets', tone: 'info', icon: LifeBuoy, title: t('live.attention.tickets', { count: answered.length }), to: '/station/tickets' });
    return list;
  }, [deviceStates, agents.data, held.data, tickets.data, now, t, stationName]);

  const lastPayout = payouts.data?.items[0];
  const loadingTotals = !perStation.data && !perStation.error;

  return (
    <>
      <PageHeader title={t('live.home.title')} description={<DayText day={today} />} />

      {/* Today */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        {loadingTotals ? (
          Array.from({ length: 4 }, (_, index) => <Skeleton key={index} className={cn('h-28', index === 0 && 'col-span-2 lg:col-span-1')} />)
        ) : perStation.error && !perStation.data ? (
          <ErrorState onRetry={perStation.reload} className="col-span-2 lg:col-span-4" />
        ) : (
          <>
            <KpiTile emphasis className="col-span-2 lg:col-span-1" label={t('live.home.valueToday')} value={<Money value={totals.amount} />} />
            <KpiTile label={t('live.home.litresToday')} value={<Volume value={totals.volume} fractionDigits={0} />} />
            <KpiTile
              label={t('live.home.fuelingsToday')}
              value={formatNumber(totals.fuelings, locale)}
              hint={totals.declined > 0 ? t('live.home.declined', { count: totals.declined }) : undefined}
            />
            <KpiTile
              className="col-span-2 lg:col-span-1"
              label={t('live.home.terminals')}
              value={devices.data ? t('live.home.onlineOf', { online, total: working.length }) : '—'}
              hint={working.length - online > 0 ? t('live.home.offlineCount', { count: working.length - online }) : t('live.home.allOnline')}
            />
          </>
        )}
      </div>

      <div className="mt-8 grid grid-cols-1 gap-8 lg:grid-cols-3">
        {/* Per station */}
        <PageSection title={t('live.home.byStation')} className="min-w-0 lg:col-span-2">
          <ListPanel>
            {!perStation.data ? (
              perStation.error ? (
                <ErrorState onRetry={perStation.reload} className="py-6" />
              ) : (
                <div className="flex flex-col gap-2 p-4" aria-busy>
                  {Array.from({ length: 3 }, (_, index) => (
                    <Skeleton key={index} className="h-14" />
                  ))}
                </div>
              )
            ) : perStation.data.length === 0 ? (
              <EmptyState title={t('live.home.noStations')} description={t('live.home.noStationsHint')} />
            ) : (
              <ul className="divide-y divide-border">
                {perStation.data.map((line) => {
                  const stationDevices = deviceStates.filter((item) => item.device.stationId === line.id);
                  const up = stationDevices.filter((item) => item.health === 'ONLINE').length;
                  const live = stationDevices.filter((item) => item.health !== 'REVOKED' && item.health !== 'AWAITING_ACTIVATION' && item.health !== 'CODE_EXPIRED').length;
                  return (
                    <li key={line.id} className="@container">
                      <div className="flex flex-col gap-2 px-4 py-3.5 @lg:flex-row @lg:items-center @lg:gap-6 @md:px-5">
                        <div className="min-w-0 flex-1">
                          <p className="truncate font-medium">{stationName(line.id)}</p>
                          <p className="text-xs text-muted">
                            {t('live.home.stationLine', { fuelings: formatNumber(line.day?.completed ?? 0, locale) })}
                            {' · '}
                            {t('live.home.devicesUp', { online: up, total: live })}
                          </p>
                        </div>
                        <dl className="grid grid-cols-3 gap-3 text-sm @lg:w-[26rem] @lg:shrink-0">
                          <div>
                            <dt className="text-xs text-muted">{t('live.home.litres')}</dt>
                            <dd className="font-semibold tabular-nums">
                              <Volume value={line.day?.volumeMl ?? 0} fractionDigits={0} />
                            </dd>
                          </div>
                          <div>
                            <dt className="text-xs text-muted">{t('live.home.value')}</dt>
                            <dd className="font-semibold tabular-nums">
                              <Money value={line.day?.amountPiastres ?? 0} />
                            </dd>
                          </div>
                          <div>
                            <dt className="text-xs text-muted">{t('live.home.owed')}</dt>
                            <dd className="font-semibold tabular-nums">
                              <Money value={line.payable.balancePiastres} />
                            </dd>
                          </div>
                        </dl>
                      </div>
                    </li>
                  );
                })}
              </ul>
            )}
          </ListPanel>
        </PageSection>

        {/* Money */}
        <PageSection title={t('live.home.money')} action={<SectionLink to="/station/settlements" label={t('live.home.openFinance')} />}>
          <div className="flex flex-col gap-3 rounded-container border border-border bg-surface p-4 shadow-card md:p-5">
            {loadingTotals ? (
              <Skeleton className="h-36" />
            ) : (
              <>
                <div>
                  <p className="text-sm text-muted">{t('live.home.owedTotal')}</p>
                  <p className="text-kpi font-semibold tabular-nums">
                    <Money value={totals.owed} />
                  </p>
                </div>
                <dl className="divide-y divide-border border-y border-border text-sm">
                  <div className="flex items-center justify-between gap-3 py-2.5">
                    <dt className="text-muted">{t('live.home.availableNext')}</dt>
                    <dd className="font-semibold tabular-nums">
                      <Money value={totals.available} />
                    </dd>
                  </div>
                  <div className="flex items-center justify-between gap-3 py-2.5">
                    <dt className="text-muted">{t('live.home.heldTotal')}</dt>
                    <dd className={cn('font-semibold tabular-nums', totals.held > 0 && 'text-warning')}>
                      <Money value={totals.held} />
                    </dd>
                  </div>
                </dl>
                <p className="flex items-start gap-2 text-sm text-muted">
                  <Icon icon={Landmark} size={16} className="mt-0.5 shrink-0" />
                  {lastPayout ? (
                    <span>
                      {t('live.home.lastPayout')}{' '}
                      <Money value={lastPayout.amountPiastres} className="font-medium text-foreground" /> · <DayText day={lastPayout.valueDate} />
                    </span>
                  ) : (
                    <span>{t('live.home.noPayoutYet')}</span>
                  )}
                </p>
              </>
            )}
          </div>
        </PageSection>
      </div>

      <div className="mt-8 grid grid-cols-1 gap-8 lg:grid-cols-3">
        <PageSection title={t('live.home.attention')} className="min-w-0">
          <ListPanel>
            {!devices.data && !agents.data ? (
              <div className="flex flex-col gap-2 p-4" aria-busy>
                <Skeleton className="h-12" />
                <Skeleton className="h-12" />
              </div>
            ) : attention.length === 0 ? (
              <EmptyState icon={CircleCheck} title={t('live.home.allClear')} description={t('live.home.allClearHint')} />
            ) : (
              <ul className="divide-y divide-border">
                {attention.map((item) => (
                  <li key={`${item.id}-${item.title}`}>
                    <Link to={item.to} className="flex items-start gap-3 px-4 py-3 transition-colors hover:bg-surface-hover md:px-5">
                      <span className={cn('mt-0.5 flex size-8 shrink-0 items-center justify-center rounded-full', TONE_FILL[item.tone])}>
                        <Icon icon={item.icon} size={16} />
                      </span>
                      <span className="min-w-0 flex-1">
                        <span className="block text-sm font-medium">{item.title}</span>
                        {item.detail && <span className="block text-xs text-muted">{item.detail}</span>}
                      </span>
                    </Link>
                  </li>
                ))}
              </ul>
            )}
          </ListPanel>
        </PageSection>

        <PageSection title={t('live.home.heldTitle')} action={<SectionLink to="/station/settlements" label={t('live.home.openFinance')} />} className="min-w-0">
          <ListPanel>
            {!held.data ? (
              held.error ? (
                <ErrorState onRetry={held.reload} className="py-6" />
              ) : (
                <div className="flex flex-col gap-2 p-4" aria-busy>
                  <Skeleton className="h-14" />
                  <Skeleton className="h-14" />
                </div>
              )
            ) : held.data.length === 0 ? (
              <EmptyState icon={CircleCheck} title={t('live.home.noHeld')} description={t('live.home.noHeldHint')} />
            ) : (
              <ul className="divide-y divide-border">
                {held.data.slice(0, HELD_SHOWN).map((item) => (
                  <li key={item.entry.id} className="flex flex-col gap-1.5 px-4 py-3 md:px-5">
                    <div className="flex items-center justify-between gap-3">
                      {item.entry.fueling && <Plate value={item.entry.fueling.plate} size="sm" />}
                      <span className="font-semibold tabular-nums">
                        <Money value={item.entry.amountPiastres} />
                      </span>
                    </div>
                    <p className="text-xs text-muted">
                      <bdi dir="ltr">{item.entry.fueling?.reference}</bdi> · {stationName(item.stationId)} · <Stamp value={item.entry.postedAt} />
                    </p>
                    <div className="flex flex-wrap gap-1.5">
                      {(item.fueling?.conflicts ?? []).length > 0 ? (
                        item.fueling!.conflicts!.map((code) => <StatusChip key={code} tone="warning" label={t(`live:fuelings.conflict.${code}`)} />)
                      ) : (
                        <StatusChip tone="warning" label={t('live.home.heldReview')} />
                      )}
                    </div>
                  </li>
                ))}
                {held.data.length > HELD_SHOWN && (
                  <li className="px-4 py-3 text-sm text-muted md:px-5">{t('live.home.moreHeld', { count: held.data.length - HELD_SHOWN })}</li>
                )}
              </ul>
            )}
          </ListPanel>
        </PageSection>

        <PageSection title={t('live.home.onShift')} action={<SectionLink to="/station/shifts" label={t('live.home.openShifts')} />} className="min-w-0">
          <ListPanel>
            {!shifts.data ? (
              shifts.error ? (
                <ErrorState onRetry={shifts.reload} className="py-6" />
              ) : (
                <div className="flex flex-col gap-2 p-4" aria-busy>
                  <Skeleton className="h-12" />
                </div>
              )
            ) : onShift.length === 0 ? (
              <EmptyState icon={UserRound} title={t('live.home.nobodyOnShift')} description={t('live.home.nobodyOnShiftHint')} />
            ) : (
              <ul className="divide-y divide-border">
                {onShift.map((shift) => (
                  <li key={shift.id} className="flex items-center gap-3 px-4 py-3 md:px-5">
                    <span className="flex size-9 shrink-0 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
                      <Icon icon={UserRound} size={18} />
                    </span>
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-sm font-medium">{shift.agentName}</p>
                      <p className="truncate text-xs text-muted">
                        <Icon icon={deviceLabel(shift.deviceId) ? Smartphone : MonitorSmartphone} size={12} className="me-1 inline" />
                        {deviceLabel(shift.deviceId) ?? shift.deviceCode} · {stationName(shift.stationId)}
                      </p>
                    </div>
                    <span className="text-xs text-muted">
                      {t('live.home.since')} <Stamp value={shift.startedAt} variant="time" />
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </ListPanel>
        </PageSection>
      </div>
    </>
  );
}

const TONE_FILL: Record<Tone, string> = {
  success: 'bg-success-subtle text-success',
  warning: 'bg-warning-subtle text-warning',
  danger: 'bg-danger-subtle text-danger',
  info: 'bg-info-subtle text-info',
  neutral: 'bg-surface-sunken text-muted',
  brand: 'bg-primary-subtle text-primary-subtle-foreground',
};

function SectionLink({ to, label }: { to: string; label: string }) {
  return (
    <Link to={to} className="inline-flex min-h-10 items-center gap-1 rounded-control px-2 text-sm font-medium text-primary-subtle-foreground hover:bg-surface-hover">
      {label}
      <Icon icon={ArrowUpRight} size={16} directional />
    </Link>
  );
}
