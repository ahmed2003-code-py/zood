import { CircleCheck, CircleX, Info, TriangleAlert } from 'lucide-react';
import type { LucideIcon } from './Icon';

export type Tone = 'success' | 'warning' | 'danger' | 'info' | 'neutral' | 'brand';

/** Default icon per tone: status is always icon + text + color. */
export const TONE_ICON: Record<Tone, LucideIcon | undefined> = {
  success: CircleCheck,
  warning: TriangleAlert,
  danger: CircleX,
  info: Info,
  neutral: undefined,
  brand: undefined,
};
