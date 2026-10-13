import { useState } from 'react';

/** A stable "now" for the lifetime of a screen, so rendering stays pure (no clock reads in render). */
export function useNow(): Date {
  const [now] = useState(() => new Date());
  return now;
}
