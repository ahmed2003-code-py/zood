import { usePhaseCycle } from '@/public/motion';

/** The sign-in vignette's loop: tag read, litres counted, wallet debited, receipt issued. */
export type VignettePhase = 'idle' | 'tag' | 'litres' | 'wallet' | 'receipt';
export const ORDER: readonly VignettePhase[] = ['idle', 'tag', 'litres', 'wallet', 'receipt'];
export const DURATION: Record<VignettePhase, number> = { idle: 900, tag: 1600, litres: 3000, wallet: 1900, receipt: 4200 };

export const at = (phase: VignettePhase, target: VignettePhase) => ORDER.indexOf(phase) >= ORDER.indexOf(target);

/** The current step of the loop; the finished fill-up when motion is off. */
export function useVignettePhase(running: boolean, reducedMotion: boolean): VignettePhase {
  const phase = usePhaseCycle(ORDER, DURATION, running && !reducedMotion);
  return reducedMotion ? 'receipt' : phase;
}

