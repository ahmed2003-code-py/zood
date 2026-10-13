import { useState } from 'react';

/** Class applied once to data that arrived while the screen was open (e.g. a POS transaction). */
export const ARRIVAL_CLASS = 'animate-[zood-arrive_2.4s_ease-out]';

/**
 * Remembers which ids were present when the screen mounted. Anything that appears later is "new":
 * its element mounts with ARRIVAL_CLASS, so the highlight plays exactly once.
 */
export function useArrivals(initialIds: readonly string[]): (id: string) => boolean {
  const [known] = useState(() => new Set(initialIds));
  return (id) => !known.has(id);
}
