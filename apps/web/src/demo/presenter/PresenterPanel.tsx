import { Monitor, Moon, Play, Presentation, RotateCcw, Siren, Sun, X } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router';
import { Button, IconButton, Icon, LanguageSwitcher, SegmentedControl, toast } from '@/design-system';
import { DEMO_AGENT_PIN, DEMO_IDS } from '@/mocks/seed';
import { useDemoStore, type NetworkMode, type PrinterMode } from '@/mocks/store';
import { usePosTheme } from '@/theme/pos-theme';
import { useTheme, type ThemePreference } from '@/theme/theme-context';

type Perspective = 'company' | 'station' | 'ops' | 'pos';

/** Seeded tags the presenter can "hold" to the simulated RFID reader, one per demo scenario. */
const DEMO_TAGS = [
  { key: 'corolla', vehicleId: DEMO_IDS.vehicleCorolla },
  { key: 'offline', vehicleId: DEMO_IDS.vehicleOffline },
  { key: 'fuso', vehicleId: DEMO_IDS.vehicleFuso },
  { key: 'vip', vehicleId: DEMO_IDS.vehicleVip },
  { key: 'delta', vehicleId: DEMO_IDS.vehicleDelta },
  { key: 'sinai', vehicleId: DEMO_IDS.vehicleSinai },
  { key: 'anomaly', vehicleId: DEMO_IDS.vehicleAnomaly },
] as const;

const PERSPECTIVE_PATH: Record<Perspective, string> = {
  company: '/company',
  station: '/station',
  ops: '/ops',
  pos: '/pos',
};

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1.5">
      <span className="text-xs font-medium text-muted">{label}</span>
      {children}
    </div>
  );
}

/**
 * Presenter controls for the investor demo. Deliberately styled as tooling (dashed accent border,
 * "Presenter" label) so it can never be mistaken for part of the customer product.
 */
export function PresenterPanel({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation(['presenter', 'settings']);
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const simulation = useDemoStore((state) => state.simulation);
  const setNetwork = useDemoStore((state) => state.setNetwork);
  const setPrinter = useDemoStore((state) => state.setPrinter);
  const reset = useDemoStore((state) => state.reset);
  const selectTag = useDemoStore((state) => state.selectTag);
  const triggerTankAnomaly = useDemoStore((state) => state.triggerTankAnomaly);
  const vehicles = useDemoStore((state) => state.data.vehicles);
  const appTheme = useTheme();
  const posTheme = usePosTheme();

  const perspective = (Object.keys(PERSPECTIVE_PATH) as Perspective[]).find((key) => pathname.startsWith(PERSPECTIVE_PATH[key]));
  // On the POS the theme control drives the POS's own theme (it is independent from the portals).
  const theme = perspective === 'pos' ? posTheme : appTheme;

  return (
    <aside
      aria-label={t('title')}
      className="fixed end-4 bottom-4 z-[60] flex max-h-[calc(100dvh-2rem)] w-[min(21rem,calc(100vw-2rem))] flex-col gap-4 overflow-y-auto rounded-container border-2 border-dashed border-accent bg-surface-elevated p-4 text-foreground shadow-elevated"
    >
      <div className="flex items-start justify-between gap-2">
        <div className="flex items-center gap-2">
          <Icon icon={Presentation} className="text-accent" />
          <div>
            <p className="font-semibold">{t('title')}</p>
            <p className="text-xs text-muted">{t('hint')}</p>
          </div>
        </div>
        <IconButton icon={X} label={t('close')} onClick={onClose} className="-me-2 -mt-2" />
      </div>

      <Field label={t('perspective')}>
        <SegmentedControl
          label={t('perspective')}
          value={perspective ?? ('' as Perspective)}
          onValueChange={(next) => navigate(PERSPECTIVE_PATH[next])}
          options={(Object.keys(PERSPECTIVE_PATH) as Perspective[]).map((key) => ({ value: key, label: t(`perspectives.${key}`) }))}
          className="w-full"
        />
      </Field>

      <div className="grid grid-cols-2 gap-3">
        <Field label={t('connectivity')}>
          <SegmentedControl<NetworkMode>
            label={t('connectivity')}
            value={simulation.network}
            onValueChange={setNetwork}
            options={[
              { value: 'online', label: t('online') },
              { value: 'offline', label: t('offline') },
            ]}
            className="w-full"
          />
        </Field>
        <Field label={t('printer')}>
          <SegmentedControl<PrinterMode>
            label={t('printer')}
            value={simulation.printer}
            onValueChange={setPrinter}
            options={[
              { value: 'ready', label: t('printerReady') },
              { value: 'failed', label: t('printerFailed') },
            ]}
            className="w-full"
          />
        </Field>
      </div>

      <Field label={t('language')}>
        <LanguageSwitcher className="w-full" />
      </Field>

      <Field label={perspective === 'pos' ? `${t('theme')} · ${t('perspectives.pos')}` : t('theme')}>
        <SegmentedControl<ThemePreference>
          label={t('theme')}
          value={theme.preference}
          onValueChange={theme.setPreference}
          options={[
            { value: 'light', label: t('settings:display.theme.light'), icon: Sun },
            { value: 'dark', label: t('settings:display.theme.dark'), icon: Moon },
            { value: 'system', label: t('settings:display.theme.system'), icon: Monitor },
          ]}
          className="w-full"
        />
      </Field>

      <Field label={t('selectTag')}>
        <select
          value={simulation.selectedTagId}
          onChange={(event) => selectTag(event.target.value)}
          className="min-h-11 w-full rounded-control border border-input-border bg-input px-3 text-sm"
        >
          {DEMO_TAGS.map((tag) => (
            <option key={tag.key} value={`tag-${tag.vehicleId}`}>
              {`${vehicles[tag.vehicleId]?.plate ?? ''} · ${t(`scenarios.${tag.key}`)}`}
            </option>
          ))}
        </select>
      </Field>

      <Field label={t('demoState')}>
        <div className="flex flex-col gap-2">
          <Button
            leadingIcon={Play}
            onClick={() => {
              reset();
              navigate('/company');
              toast(t('prepared'), 'info');
            }}
          >
            {t('prepare')}
          </Button>
          <div className="grid grid-cols-2 gap-2">
            <Button
              variant="secondary"
              size="sm"
              leadingIcon={RotateCcw}
              onClick={() => {
                reset();
                toast(t('resetDone'), 'info');
              }}
            >
              {t('reset')}
            </Button>
            <Button
              variant="secondary"
              size="sm"
              leadingIcon={Siren}
              onClick={() => {
                const outcome = triggerTankAnomaly();
                const alert = useDemoStore.getState().data.alerts.find((item) => item.transactionId === outcome?.transaction.id);
                toast(t('alertTriggered'), 'warning');
                if (alert) navigate(`/ops/alerts/${alert.id}`);
              }}
            >
              {t('triggerAlert')}
            </Button>
          </div>
        </div>
      </Field>
      <p className="text-xs text-muted">{t('pin', { pin: DEMO_AGENT_PIN })}</p>
    </aside>
  );
}
