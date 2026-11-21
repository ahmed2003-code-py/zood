import { useEffect, useState, type RefObject } from 'react';

export { useOnScreen, usePrefersReducedMotion } from '@/public/motion';

/**
 * Scroll reveals that never leave a blank area.
 *
 * Content is visible by default. Only once this hook has run (JavaScript and IntersectionObserver
 * ready) does it mark the root with data-reveal-ready, and only then does the CSS hide the
 * [data-reveal] elements that are still below the fold. Anything already on screen or above it is
 * shown first, so nothing in view ever disappears. Safety nets: after every scroll settles, after a
 * short timeout and before printing, everything the visitor has passed is shown.
 */
export function useScrollReveal(root: RefObject<HTMLElement | null>, enabled: boolean): void {
  useEffect(() => {
    const container = root.current;
    if (!enabled || !container || typeof IntersectionObserver === 'undefined') return;
    const pending = new Set(container.querySelectorAll<HTMLElement>('[data-reveal]:not([data-revealed])'));

    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) if (entry.isIntersecting) reveal(entry.target as HTMLElement);
      },
      { rootMargin: '0px 0px -6% 0px', threshold: 0 },
    );
    function reveal(element: HTMLElement) {
      element.setAttribute('data-revealed', '');
      observer.unobserve(element);
      pending.delete(element);
    }
    /** Shows everything whose top is above the bottom of the viewport (in view or scrolled past). */
    const sweep = () => {
      const bottom = window.innerHeight;
      for (const element of pending) if (element.getBoundingClientRect().top < bottom) reveal(element);
    };
    const showAll = () => [...pending].forEach(reveal);

    sweep();
    for (const element of pending) observer.observe(element);
    container.setAttribute('data-reveal-ready', '');

    let settle = 0;
    const onScroll = () => {
      window.clearTimeout(settle);
      settle = window.setTimeout(sweep, 140);
    };
    const safety = window.setTimeout(sweep, 1200);
    window.addEventListener('scroll', onScroll, { passive: true });
    window.addEventListener('beforeprint', showAll);
    return () => {
      observer.disconnect();
      window.clearTimeout(settle);
      window.clearTimeout(safety);
      window.removeEventListener('scroll', onScroll);
      window.removeEventListener('beforeprint', showAll);
      container.removeAttribute('data-reveal-ready');
    };
  }, [root, enabled]);
}

/**
 * The page section the visitor is reading: the last one (in page order) that crosses a thin band
 * a little above the middle of the viewport. Null over the hero and the sections without a link.
 */
export function useActiveSection<T extends string>(ids: readonly T[]): T | null {
  const [active, setActive] = useState<T | null>(null);
  useEffect(() => {
    if (typeof IntersectionObserver === 'undefined') return;
    const crossing = new Set<T>();
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          const id = entry.target.id as T;
          if (entry.isIntersecting) crossing.add(id);
          else crossing.delete(id);
        }
        setActive(ids.findLast((id) => crossing.has(id)) ?? null);
      },
      { rootMargin: '-38% 0px -58% 0px' },
    );
    for (const id of ids) {
      const section = document.getElementById(id);
      if (section) observer.observe(section);
    }
    return () => observer.disconnect();
  }, [ids]);
  return active;
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
