/**
 * The live terminal's frame: one screen at a time with its main action at the bottom, the header
 * (station, agent, connection, menu), the offline banner, and the agent menu with language, theme
 * and the sync panel. Built for outdoor use: Light by default, large targets, status always as
 * icon + word + colour.
 */
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { CloudOff, Download, History, LogOut, Menu, RefreshCw, Wifi, WifiOff, X } from 'lucide-react';
import { Button, Icon, IconButton, LanguageSwitcher, StatusChip, ThemeSwitcher } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { cn } from '@/lib/cn';
import { formatNumber } from '@/lib/format';
import { useErrorText } from './use-error-text';
import type { OfflineState } from './use-offline';

/** One focused task; the main action sits at the bottom, within reach of the thumb. */
export function Screen({ children, actions, className }: { children: ReactNode; actions?: ReactNode; className?: string }) {
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className={cn('flex min-h-0 flex-1 flex-col items-center gap-4 overflow-y-auto px-5 py-5 text-center', className)}>{children}</div>
      {actions && <div className="flex flex-col gap-2 border-t border-border bg-background px-4 pt-3 pb-4">{actions}</div>}
    </div>
  );
}

/**
 * A step of the flow. Keyed by the step, so each new step fades in (150 ms); reduced motion turns
 * it off globally (index.css), and nothing depends on it running.
 */
export function Step({ id, children }: { id: string; children: ReactNode }) {
  return (
    <div key={id} data-step={id} className="flex min-h-0 flex-1 flex-col animate-[zood-fade-in_150ms_ease-out]">
      {children}
    </div>
  );
}

export function ErrorLine({ text }: { text: string | undefined }) {
  return (
    <p role="alert" className="min-h-5 text-sm font-medium text-danger">
      {text ?? ''}
    </p>
  );
}

/** A round status mark for a result screen; it settles in once (zood-dialog-in). */
export function ResultMark({ tone, icon, size = 'lg' }: { tone: 'success' | 'warning' | 'danger' | 'brand'; icon: Parameters<typeof Icon>[0]['icon']; size?: 'md' | 'lg' }) {
  const fill = {
    success: 'bg-success-subtle text-success',
    warning: 'bg-warning-subtle text-warning',
    danger: 'bg-danger-subtle text-danger',
    brand: 'bg-primary-subtle text-primary-subtle-foreground',
  }[tone];
  return (
    <span
      aria-hidden
      className={cn(
        'flex shrink-0 items-center justify-center rounded-full animate-[zood-dialog-in_220ms_cubic-bezier(0.22,1,0.36,1)]',
        size === 'lg' ? 'size-20' : 'size-14',
        fill,
      )}
    >
      <Icon icon={icon} size={size === 'lg' ? 44 : 30} />
    </span>
  );
}

/** Online or offline, and how many fuelings wait to be sent: always visible in the header. */
function ConnectionBadge({ state }: { state: OfflineState }) {
  const { t } = useTranslation('pos');
  const { locale } = useLocale();
  const pending = state.queue.length;
  if (state.syncing) return <StatusChip tone="info" size="md" icon={RefreshCw} label={t('live.header.syncing')} />;
  return (
    <span className="flex items-center gap-1.5" data-testid="pos-connection">
      <StatusChip
        tone={state.online ? 'success' : 'warning'}
        size="md"
        icon={state.online ? Wifi : WifiOff}
        label={t(state.online ? 'live.offline.online' : 'live.offline.offline')}
      />
      {pending > 0 && (
        <span
          className="flex h-7 min-w-7 items-center justify-center rounded-full bg-warning-subtle px-2 text-sm font-semibold text-warning tabular-nums"
          aria-label={t('live.offline.pending', { count: pending })}
        >
          {formatNumber(pending, locale)}
        </span>
      )}
    </span>
  );
}

export interface HeaderAgent {
  name: string;
  workerId: string;
}

/**
 * Station and agent at the start, the connection and the menu at the end. The menu is there before
 * sign-in too, so the language can be switched on the sign-in screen.
 */
export function PosHeader({
  title,
  agent,
  state,
  menu,
}: {
  title: string;
  agent?: HeaderAgent;
  state?: OfflineState;
  menu: (close: () => void) => ReactNode;
}) {
  const { t } = useTranslation('pos');
  const [open, setOpen] = useState(false);
  return (
    <>
      <header className="flex min-h-16 items-center gap-2 border-b border-border bg-surface px-4 py-2.5">
        <div className="min-w-0 flex-1">
          <p className="truncate font-semibold">{title}</p>
          {agent && (
            <p className="truncate text-sm text-muted">
              {agent.name} · <bdi dir="ltr">{agent.workerId}</bdi>
            </p>
          )}
        </div>
        {state && <ConnectionBadge state={state} />}
        <IconButton icon={Menu} label={t('menu')} onClick={() => setOpen(true)} className="-me-2 size-12" />
      </header>
      {state && !state.online && (
        <p role="status" className="flex items-center gap-2 bg-warning-subtle px-4 py-2 text-sm font-medium text-warning">
          <Icon icon={CloudOff} size={18} />
          {t('live.header.offlineBanner')}
        </p>
      )}
      {open && <PosMenu onClose={() => setOpen(false)}>{menu(() => setOpen(false))}</PosMenu>}
    </>
  );
}

/** A bottom sheet inside the terminal's frame. Escape and a tap outside close it. */
export function PosMenu({ onClose, children }: { onClose: () => void; children: ReactNode }) {
  const { t } = useTranslation('pos');
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
        className="mt-auto flex max-h-[90%] flex-col gap-4 overflow-y-auto rounded-t-dialog bg-surface-elevated p-5 shadow-dialog animate-[zood-sheet-from-bottom_240ms_cubic-bezier(0.22,1,0.36,1)]"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-center justify-between gap-3">
          <p className="text-title font-semibold">{t('menu')}</p>
          <IconButton ref={closeRef} icon={X} label={t('closeMenu')} onClick={onClose} className="-me-2 size-12" />
        </div>
        {children}
      </div>
    </div>
  );
}

/** Language and theme, the same controls as the portals, at the terminal's size. */
export function DisplayPreferences() {
  const { t } = useTranslation('settings');
  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-col gap-1.5">
        <span className="text-sm font-medium text-muted">{t('display.language')}</span>
        <LanguageSwitcher className="w-full" />
      </div>
      <div className="flex flex-col gap-1.5">
        <span className="text-sm font-medium text-muted">{t('display.appearance')}</span>
        <ThemeSwitcher className="w-full" />
      </div>
    </div>
  );
}

/** Menu buttons for the shift: history and end of shift. */
export function ShiftMenuActions({ onHistory, onEndShift }: { onHistory?: () => void; onEndShift: () => void }) {
  const { t } = useTranslation('pos');
  return (
    <div className="grid grid-cols-1 gap-2">
      {onHistory && (
        <Button variant="secondary" size="lg" fullWidth leadingIcon={History} onClick={onHistory}>
          {t('shiftHistory')}
        </Button>
      )}
      <Button variant="secondary" size="lg" fullWidth leadingIcon={LogOut} onClick={onEndShift}>
        {t('endShift')}
      </Button>
    </div>
  );
}

/** What waits to be sent, what the server refused, the last sync, and the simulator's offline switch. */
export function SyncPanel({ state }: { state: OfflineState }) {
  const { t } = useTranslation('pos');
  const errorText = useErrorText();
  const pending = state.queue.length;
  return (
    <section aria-label={t('live.sync.title')} className="flex w-full flex-col gap-3 rounded-container border border-border bg-surface p-4 text-start">
      <div className="flex items-center justify-between gap-2">
        <p className="font-semibold">{t('live.sync.title')}</p>
        <StatusChip
          tone={state.online ? 'success' : 'warning'}
          icon={state.online ? Wifi : WifiOff}
          label={t(state.online ? 'live.offline.online' : 'live.offline.offline')}
        />
      </div>
      <p className="text-sm text-muted">{pending > 0 ? t('live.offline.pending', { count: pending }) : t('live.sync.nothingWaiting')}</p>
      {!state.snapshot && <p className="text-sm text-muted">{t('live.offline.noSnapshot')}</p>}
      {state.rejectedCount > 0 && (
        <p role="alert" className="text-sm font-medium text-danger">
          {t('live.offline.rejected', { count: state.rejectedCount })}
        </p>
      )}
      {state.resolvedCount > 0 && <p className="text-sm text-muted">{t('live.offline.resolved', { count: state.resolvedCount })}</p>}
      {state.lastError && (
        <p role="alert" className="text-sm text-danger">
          {t('live.offline.syncFailed', { reason: errorText(state.lastError) })}
        </p>
      )}
      {state.lastSync && (
        <p className="text-sm text-muted">
          {t('live.offline.lastSync', { recorded: state.lastSync.recorded, duplicates: state.lastSync.duplicates, rejected: state.lastSync.rejected })}
        </p>
      )}
      <div className="flex flex-col gap-2">
        {state.online && pending > 0 && (
          <Button variant="secondary" size="lg" leadingIcon={RefreshCw} loading={state.syncing} onClick={() => void state.sync()}>
            {t('live.offline.syncNow')}
          </Button>
        )}
        {(pending > 0 || state.rejectedCount > 0) && (
          <Button variant="ghost" size="lg" leadingIcon={Download} onClick={() => void state.exportQueue()}>
            {t('live.offline.export')}
          </Button>
        )}
        <Button variant="ghost" size="lg" leadingIcon={state.forcedOffline ? Wifi : WifiOff} onClick={() => state.setForcedOffline(!state.forcedOffline)}>
          {t(state.forcedOffline ? 'live.offline.simulateOnline' : 'live.offline.simulateOffline')}
        </Button>
      </div>
    </section>
  );
}
