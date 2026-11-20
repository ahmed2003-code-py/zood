import { useEffect, useState, useSyncExternalStore, type RefObject } from 'react';

/**
 * Motion helpers shared by the public pages (landing, sign-in, legal). The portals never import
 * this file, so none of it reaches their chunks.
 */

const REDUCED_MOTION = '(prefers-reduced-motion: reduce)';

function subscribeReducedMotion(onChange: () => void): () => void {
  const media = window.matchMedia(REDUCED_MOTION);
  media.addEventListener('change', onChange);
  return () => media.removeEventListener('change', onChange);
}

export function prefersReducedMotion(): boolean {
  return window.matchMedia(REDUCED_MOTION).matches;
}

/** True when the visitor asked for less motion. Follows the setting live. */
export function usePrefersReducedMotion(): boolean {
  return useSyncExternalStore(subscribeReducedMotion, prefersReducedMotion, () => false);
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
 * Steps through `order`, holding each phase for its duration, and loops. Holds still while
 * `running` is false (off screen, background tab) and resumes where it was.
 */
export function usePhaseCycle<P extends string>(order: readonly P[], durations: Record<P, number>, running: boolean): P {
  const [phase, setPhase] = useState<P>(order[0] as P);
  useEffect(() => {
    if (!running) return;
    const timer = window.setTimeout(() => {
      setPhase((current) => order[(order.indexOf(current) + 1) % order.length] ?? (order[0] as P));
    }, durations[phase]);
    return () => window.clearTimeout(timer);
  }, [order, durations, phase, running]);
  return phase;
}

export const THEME_FADE_ATTRIBUTE = 'data-public-theme-fade';
const THEME_FADE_MS = 240;

/**
 * Light ↔ Dark on a public page: a short cross-fade of colours while the theme changes, and only
 * then (src/public/stage.css). Under reduced motion the switch stays instant.
 *
 * The attribute goes on <html> in the same task as the theme change (a MutationObserver callback
 * runs before the next style update), so the browser transitions from the old colours to the new.
 */
export function useThemeCrossfade(): void {
  useEffect(() => {
    const root = document.documentElement;
    let theme = root.dataset.theme;
    let timer = 0;
    const observer = new MutationObserver(() => {
      if (root.dataset.theme === theme) return;
      theme = root.dataset.theme;
      if (prefersReducedMotion()) return;
      root.setAttribute(THEME_FADE_ATTRIBUTE, '');
      window.clearTimeout(timer);
      timer = window.setTimeout(() => root.removeAttribute(THEME_FADE_ATTRIBUTE), THEME_FADE_MS);
    });
    observer.observe(root, { attributes: true, attributeFilter: ['data-theme'] });
    return () => {
      observer.disconnect();
      window.clearTimeout(timer);
      root.removeAttribute(THEME_FADE_ATTRIBUTE);
    };
  }, []);
}
