/**
 * Station POS simulator (lazy-loaded chunk). A focused kiosk experience, not a portal: no sidebar,
 * no bottom navigation. Shown inside a handheld device frame from 768px; full-screen below.
 * The POS keeps its own theme (default Light, for outdoor readability).
 *
 * RFID is simulated: "Scan vehicle" reads the tag selected in the presenter panel (default the hero
 * vehicle). Production uses the terminal's NFC hardware behind a TagReader interface.
 */
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useDemoStore } from '@/mocks/store';
import { usePosTheme } from '@/theme/pos-theme';
import { ThemeProvider } from '@/theme/ThemeProvider';
import { PosHeader, PosStatusBanner } from './chrome';
import { HistoryScreen } from './HistoryScreen';
import { LoginScreen } from './LoginScreen';
import {
  ConfirmSheet,
  DeclinedScreen,
  FuelEntryScreen,
  HomeScreen,
  ProcessingScreen,
  ReceiptScreen,
  ScanningScreen,
  SuccessScreen,
  VerifyScreen,
} from './screens';
import { usePosSession } from './session';
import { usePosFlow } from './use-pos-flow';

function PosFlow({ agentId }: { agentId: string }) {
  const { step, setStep, goHome, startScan, toFuel, review, confirm } = usePosFlow(agentId);
  const signOut = usePosSession((state) => state.signOut);

  switch (step.name) {
    case 'home':
      return <HomeScreen agentId={agentId} onScan={startScan} onHistory={() => setStep({ name: 'history' })} onEndShift={signOut} />;
    case 'scanning':
      return <ScanningScreen onCancel={goHome} />;
    case 'verify':
      return <VerifyScreen scan={step.scan} onContinue={() => toFuel(step.scan)} onCancel={goHome} />;
    case 'fuel':
      return <FuelEntryScreen scan={step.scan} onReview={(volumeMl) => review(step.scan, volumeMl)} onBack={goHome} />;
    case 'confirm':
      return (
        <>
          <FuelEntryScreen scan={step.scan} onReview={() => undefined} onBack={goHome} />
          <ConfirmSheet step={step} onConfirm={() => void confirm(step)} onBack={() => toFuel(step.scan)} />
        </>
      );
    case 'processing':
      return <ProcessingScreen offline={step.offline} />;
    case 'success':
      return (
        <SuccessScreen
          receipt={step.receipt}
          offline={step.offline}
          onFinish={goHome}
          onViewReceipt={() => setStep({ name: 'receipt', receipt: step.receipt, back: 'success', offline: step.offline })}
        />
      );
    case 'declined':
      return <DeclinedScreen step={step} onBack={goHome} />;
    case 'receipt':
      return (
        <ReceiptScreen
          receipt={step.receipt}
          onBack={() =>
            step.back === 'history' ? setStep({ name: 'history' }) : setStep({ name: 'success', receipt: step.receipt, offline: step.offline ?? false })
          }
        />
      );
    case 'history':
      return <HistoryScreen agentId={agentId} onOpen={(receipt) => setStep({ name: 'receipt', receipt, back: 'history' })} onBack={goHome} />;
  }
}

/** If the window reloaded during a sync, or the network returned while no window was syncing, resume. */
function useSyncRecovery() {
  useEffect(() => {
    const { sync, posQueue, simulation } = useDemoStore.getState();
    if (sync.status === 'syncing') useDemoStore.setState({ sync: { ...sync, status: 'idle' } });
    if (posQueue.length > 0 && simulation.network === 'online') useDemoStore.getState().syncQueue();
  }, []);
}

export function PosApp() {
  const { t } = useTranslation('pos');
  const { preference, setPreference } = usePosTheme();
  const agentId = usePosSession((state) => state.agentId);
  useSyncRecovery();
  return (
    <div className="min-h-dvh md:flex md:flex-col md:items-center md:justify-center md:gap-4 md:bg-surface-sunken md:p-6">
      <div
        className="md:h-[min(52rem,calc(100dvh-5rem))] md:w-[24.5rem] md:rounded-[3rem] md:bg-device-bezel md:p-3 md:shadow-elevated"
        aria-label={t('frameLabel')}
        role="region"
        data-shell="pos-device"
      >
        <ThemeProvider
          scope
          surface="pos"
          preference={preference}
          onPreferenceChange={setPreference}
          className="relative flex h-dvh flex-col md:h-full md:overflow-hidden md:rounded-[2.25rem]"
        >
          <PosHeader />
          <PosStatusBanner />
          {agentId ? <PosFlow key={agentId} agentId={agentId} /> : <LoginScreen />}
        </ThemeProvider>
      </div>
      <p className="hidden text-xs text-muted md:block">{t('frameLabel')}</p>
    </div>
  );
}
