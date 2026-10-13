import type { LocalizedText } from '@/lib/locale';
import { DEMO_IDS } from './seed';

export type PersonaRole = 'fleetManager' | 'stationManager' | 'operations' | 'agent';

/** The signed-in user for each demo perspective (no real authentication in the demo). */
export interface Persona {
  name: LocalizedText;
  role: PersonaRole;
  /** Initials for the avatar, per language. */
  initials: LocalizedText;
}

export const PERSONAS = {
  company: { name: { ar: 'خالد فؤاد', en: 'Khaled Fouad' }, role: 'fleetManager', initials: { ar: 'خ', en: 'KF' } },
  station: { name: { ar: 'عمرو حسن', en: 'Amr Hassan' }, role: 'stationManager', initials: { ar: 'ع', en: 'AH' } },
  ops: { name: { ar: 'سارة منصور', en: 'Sara Mansour' }, role: 'operations', initials: { ar: 'س', en: 'SM' } },
} as const satisfies Record<string, Persona>;

/** The organization each perspective belongs to in the demo. */
export const DEMO_CONTEXT = {
  companyId: DEMO_IDS.company,
  stationId: DEMO_IDS.station,
  posDeviceId: DEMO_IDS.device,
  posAgentId: DEMO_IDS.agent,
} as const;
