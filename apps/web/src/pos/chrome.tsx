import { CircleCheck, LogOut, Menu, Printer, RefreshCw, WifiOff, X } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ConnectivityChip, Icon, IconButton, LanguageSwitcher, StatusChip, ThemeSwitcher } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { localize } from '@/lib/locale';
import { DEMO_CONTEXT } from '@/mocks/personas';
import { useDemoStore } from '@/mocks/store';
import { usePosSession } from './session';

function PosMenu({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation(['pos', 'settings']);
  const { locale } = useLocale();
  const agentId = usePosSession((state) => state.agentId);
  const signOut = usePosSession((state) => state.signOut);
  const agent = useDemoStore((state) => (agentId ? state.data.agents[agentId] : undefined));
  const printer = useDemoStore((state) => state.simulation.printer);
  const closeRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    closeRef.current?.focus();
    const onKey = (event: KeyboardEvent) => event.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="absolute inset-0 z-20 flex flex-col bg-overlay animate-[zood-fade-in_120ms_ease-out]" onClick={onClose}>
      <div
        role="dialog"
        aria-modal="true"
        aria-label={t('menu')}
        className="mt-auto flex flex-col gap-5 rounded-t-2xl bg-surface-elevated p-5 animate-[zood-sheet-from-bottom_200ms_ease-out]"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <p className="truncate text-lg font-semibold">{localize(agent?.name, locale)}</p>
            <p className="text-sm text-muted">{t('workerId', { id: agent?.workerId ?? '' })}</p>
          </div>
          <IconButton ref={closeRef} icon={X} label={t('closeMenu')} onClick={onClose} className="-me-2 -mt-1" />
        </div>
        <StatusChip
          size="md"
          tone={printer === 'ready' ? 'success' : 'danger'}
          icon={Printer}
          label={t(printer === 'ready' ? 'printer.ready' : 'printer.failed')}
          className="self-start"
        />
        <div className="flex flex-col gap-1.5">
          <span className="text-sm font-medium text-muted">{t('settings:display.language')}</span>
          <LanguageSwitcher className="w-full" />
        </div>
        <div className="flex flex-col gap-1.5">
          <span className="text-sm font-medium text-muted">{t('settings:display.appearance')}</span>
          <ThemeSwitcher className="w-full" />
        </div>
        <button
          type="button"
          onClick={signOut}
          className="flex min-h-14 items-center justify-center gap-2 rounded-control border border-border-strong text-base font-semibold transition-colors hover:bg-surface-sunken"
        >
          <Icon icon={LogOut} directional />
          {t('signOut')}
        </button>
      </div>
    </div>
  );
}

/** Persistent POS header: station, agent, connectivity, menu. */
export function PosHeader() {
  const { t } = useTranslation('pos');
  const { locale } = useLocale();
  const [menuOpen, setMenuOpen] = useState(false);
  const agentId = usePosSession((state) => state.agentId);
  const agent = useDemoStore((state) => (agentId ? state.data.agents[agentId] : undefined));
  const station = useDemoStore((state) => state.data.stations[DEMO_CONTEXT.stationId]);
  const online = useDemoStore((state) => state.simulation.network === 'online');
  return (
    <>
      <header className="flex items-center gap-3 border-b border-border px-4 py-3">
        <div className="min-w-0 flex-1">
          <p className="truncate font-semibold">{localize(station?.name, locale)}</p>
          {agent && <p className="truncate text-sm text-muted">{localize(agent.name, locale)}</p>}
        </div>
        <ConnectivityChip online={online} />
        {agent && <IconButton icon={Menu} label={t('menu')} onClick={() => setMenuOpen(true)} className="-me-2" />}
      </header>
      {menuOpen && <PosMenu onClose={() => setMenuOpen(false)} />}
    </>
  );
}

/** Offline mode and synchronization status. Never blocks the agent. */
export function PosStatusBanner() {
  const { t } = useTranslation('pos');
  const online = useDemoStore((state) => state.simulation.network === 'online');
  const sync = useDemoStore((state) => state.sync);
  const lastSyncedAt = sync.lastSyncedAt;
  // A sync that completed before this screen mounted is old news; only newly completed syncs are announced.
  const [alreadySeen] = useState(lastSyncedAt);
  const [dismissed, setDismissed] = useState<string | undefined>(undefined);
  const showDone = Boolean(lastSyncedAt) && lastSyncedAt !== alreadySeen && lastSyncedAt !== dismissed;

  useEffect(() => {
    if (!lastSyncedAt) return;
    const timer = setTimeout(() => setDismissed(lastSyncedAt), 5000);
    return () => clearTimeout(timer);
  }, [lastSyncedAt]);

  if (!online) {
    return (
      <p role="status" className="flex items-center gap-2 bg-warning-subtle px-4 py-2.5 text-sm font-medium text-warning">
        <Icon icon={WifiOff} size={18} />
        {t('offlineBanner')}
      </p>
    );
  }
  if (sync.status === 'syncing') {
    return (
      <p role="status" className="flex items-center gap-2 bg-info-subtle px-4 py-2.5 text-sm font-medium text-info">
        <Icon icon={RefreshCw} size={18} className="animate-spin" />
        {t('sync.syncing')}
      </p>
    );
  }
  if (showDone) {
    return (
      <p role="status" className="flex items-center gap-2 bg-success-subtle px-4 py-2.5 text-sm font-medium text-success">
        <Icon icon={CircleCheck} size={18} />
        {t('sync.done')}
      </p>
    );
  }
  return null;
}
