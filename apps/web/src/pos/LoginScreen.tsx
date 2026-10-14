import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Numpad, BrandLogo } from '@/design-system';
import { cn } from '@/lib/cn';
import { DEMO_CONTEXT } from '@/mocks/personas';
import { DEMO_AGENT_PIN } from '@/mocks/seed';
import { useDemoStore } from '@/mocks/store';
import { usePosSession } from './session';

const PIN_LENGTH = 6;

/** Worker ID + 6-digit PIN (PRD §6.1). Works offline: credentials are verified on the device. */
export function LoginScreen() {
  const { t } = useTranslation('pos');
  const signIn = usePosSession((state) => state.signIn);
  const agents = useDemoStore((state) => state.data.agents);
  const defaultWorker = agents[DEMO_CONTEXT.posAgentId]?.workerId ?? '';
  const [workerId, setWorkerId] = useState(defaultWorker);
  const [pin, setPin] = useState('');
  const [error, setError] = useState(false);

  const submit = (candidate: string) => {
    const agent = Object.values(agents).find((item) => item.workerId === workerId.trim() && item.stationId === DEMO_CONTEXT.stationId);
    if (agent && candidate === DEMO_AGENT_PIN) {
      signIn(agent.id);
      return;
    }
    setError(true);
    setPin('');
  };

  const addDigit = (digit: string) => {
    if (pin.length >= PIN_LENGTH) return;
    const next = pin + digit;
    setError(false);
    setPin(next);
    if (next.length === PIN_LENGTH) setTimeout(() => submit(next), 120);
  };

  return (
    <div className="flex flex-1 flex-col gap-5 overflow-y-auto px-6 py-6">
      <div className="flex flex-col items-center gap-2 pt-2">
        <BrandLogo size="md" />
        <h1 className="text-xl font-semibold">{t('login.title')}</h1>
        <p className="text-sm text-muted">{t('login.offlineCapable')}</p>
      </div>
      <label className="flex flex-col gap-1.5">
        <span className="text-sm font-medium">{t('login.workerIdLabel')}</span>
        <input
          value={workerId}
          onChange={(event) => setWorkerId(event.target.value.replace(/\D/g, ''))}
          inputMode="numeric"
          dir="ltr"
          className="min-h-12 rounded-control border border-input-border bg-input px-3 text-center text-xl font-semibold tracking-widest tabular-nums"
        />
      </label>
      <div className="flex flex-col items-center gap-2">
        <span className="text-sm font-medium">{t('login.pin')}</span>
        <div dir="ltr" className="flex gap-3" aria-hidden>
          {Array.from({ length: PIN_LENGTH }, (_, index) => (
            <span
              key={index}
              className={cn('size-3.5 rounded-full border-2', index < pin.length ? 'border-primary bg-primary' : 'border-border-strong')}
            />
          ))}
        </div>
        <p role="alert" className="min-h-5 text-sm font-medium text-danger">
          {error ? t('login.wrongPin') : ''}
        </p>
      </div>
      <Numpad onDigit={addDigit} onBackspace={() => setPin((value) => value.slice(0, -1))} />
    </div>
  );
}
