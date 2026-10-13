import { lazy, Suspense, useEffect, useState } from 'react';
import { readUrlParam } from '@/lib/storage';

const PresenterPanel = lazy(() => import('./PresenterPanel').then((module) => ({ default: module.PresenterPanel })));

/**
 * Hidden presenter tooling. Never visible during normal navigation: it opens only with
 * Ctrl+Shift+P or ?presenter=1, and its code is loaded only when opened.
 */
export function PresenterHost() {
  const [open, setOpen] = useState(() => readUrlParam('presenter') === '1');

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.ctrlKey && event.shiftKey && event.key.toLowerCase() === 'p') {
        event.preventDefault();
        setOpen((value) => !value);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  if (!open) return null;
  return (
    <Suspense fallback={null}>
      <PresenterPanel onClose={() => setOpen(false)} />
    </Suspense>
  );
}
