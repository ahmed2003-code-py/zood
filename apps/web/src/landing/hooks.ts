import { useEffect, useState, useSyncExternalStore, type RefObject } from 'react';

const REDUCED_MOTION = '(prefers-reduced-motion: reduce)';

function subscribeReducedMotion(onChange: () => void): () => void {
  const media = window.matchMedia(REDUCED_MOTION);
  media.addEventListener('change', onChange);
  return () => media.removeEventListener('change', onChange);
}

/** True when the visitor asked for less motion. Follows the setting live. */
export function usePrefersReducedMotion(): boolean {
  return useSyncExternalStore(
    subscribeReducedMotion,
    () => window.matchMedia(REDUCED_MOTION).matches,
    () => false,
  );
}

/** Whether an element is on screen (and the tab is visible), so off-screen motion can pause. */
export function useOnScreen(ref: RefObject<Element | null>, rootMargin = '0px'): boolean {
  const [intersecting, setIntersecting] = useState(true);
  const [visible, setVisible] = useState(() => document.visibilityState !== 'hidden');

  useEffect(() => {
    const element = ref.current;
    if (!element || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver(([entry]) => setIntersecting(Boolean(entry?.isIntersecting)), { rootMargin });
    observer.observe(element);
    return () => observer.disconnect();
  }, [ref, rootMargin]);

  useEffect(() => {
    const onChange = () => setVisible(document.visibilityState !== 'hidden');
    document.addEventListener('visibilitychange', onChange);
    return () => document.removeEventListener('visibilitychange', onChange);
  }, []);

  return intersecting && visible;
}

/**
 * Scroll reveals: marks every [data-reveal] element inside the root with data-revealed once it
 * scrolls into view. The CSS hides unrevealed elements only while motion is on, so the page is
 * complete without JavaScript animation and under prefers-reduced-motion.
 */
export function useScrollReveal(root: RefObject<HTMLElement | null>, enabled: boolean): void {
  useEffect(() => {
    const container = root.current;
    if (!enabled || !container || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          entry.target.setAttribute('data-revealed', '');
          observer.unobserve(entry.target);
        }
      },
      { rootMargin: '0px 0px -8% 0px', threshold: 0.12 },
    );
    container.querySelectorAll('[data-reveal]:not([data-revealed])').forEach((element) => observer.observe(element));
    return () => observer.disconnect();
  }, [root, enabled]);
}

/** The current time, refreshed every few seconds (the Cairo clock). */
export function useNow(intervalMs = 10_000): Date {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(new Date()), intervalMs);
    return () => window.clearInterval(timer);
  }, [intervalMs]);
  return now;
}
