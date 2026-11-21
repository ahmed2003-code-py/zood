import { useEffect, useRef, useState } from 'react';

const DURATION_MS = 700;

function prefersReducedMotion(): boolean {
  return typeof window === 'undefined' || window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/**
 * A KPI figure that counts up once when it first appears, then shows the exact value. The steps are
 * whole integers of the value's own unit (piastres, millilitres), so the formatter never sees a
 * fraction; the final frame is always the exact value. Reduced motion shows it at once. Screen
 * readers get the final value only.
 */
export function CountUp({ value, format, className }: { value: number; format: (value: number) => string; className?: string }) {
  const [shown, setShown] = useState(() => (prefersReducedMotion() ? value : 0));
  const played = useRef(false);

  useEffect(() => {
    if (played.current || prefersReducedMotion() || value === 0) {
      setShown(value);
      return;
    }
    played.current = true;
    let frame = 0;
    const start = performance.now();
    const step = (now: number) => {
      const progress = Math.min(1, (now - start) / DURATION_MS);
      // Ease out: fast at first, settling on the value.
      const eased = 1 - (1 - progress) ** 3;
      setShown(progress >= 1 ? value : Math.trunc(value * eased));
      if (progress < 1) frame = requestAnimationFrame(step);
    };
    frame = requestAnimationFrame(step);
    // Whatever the frame clock does (a background tab, a frozen test clock), end on the exact value.
    const settle = window.setTimeout(() => setShown(value), DURATION_MS + 150);
    return () => {
      cancelAnimationFrame(frame);
      window.clearTimeout(settle);
    };
  }, [value]);

  return (
    <span className={className}>
      <span aria-hidden>{format(shown)}</span>
      <span className="sr-only">{format(value)}</span>
    </span>
  );
}
