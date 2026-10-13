import { X } from 'lucide-react';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/cn';
import { IconButton } from './Button';
import { Icon } from './Icon';
import { useToastStore, type ToastItem } from './toast-store';
import { TONE_ICON } from './tone';

const AUTO_DISMISS_MS = 3500;

function ToastMessage({ item }: { item: ToastItem }) {
  const { t } = useTranslation();
  const dismiss = useToastStore((state) => state.dismiss);
  useEffect(() => {
    const timer = window.setTimeout(() => dismiss(item.id), AUTO_DISMISS_MS);
    return () => window.clearTimeout(timer);
  }, [dismiss, item.id]);
  const glyph = TONE_ICON[item.tone];
  return (
    <div className="pointer-events-auto flex w-full items-center gap-3 rounded-container border border-border bg-surface-elevated py-2 ps-4 pe-2 shadow-elevated animate-[zood-fade-in_150ms_ease-out]">
      {glyph && (
        <Icon
          icon={glyph}
          className={cn(
            item.tone === 'success' && 'text-success',
            item.tone === 'info' && 'text-info',
            item.tone === 'warning' && 'text-warning',
            item.tone === 'danger' && 'text-danger',
          )}
        />
      )}
      <p className="flex-1 text-sm font-medium">{item.message}</p>
      <IconButton icon={X} label={t('a11y.dismissNotification')} onClick={() => dismiss(item.id)} />
    </div>
  );
}

/** Render once near the app root. Bottom-centered, above mobile bottom navigation. */
export function Toaster() {
  const { t } = useTranslation();
  const items = useToastStore((state) => state.items);
  return (
    <div
      aria-live="polite"
      aria-label={t('a11y.notifications')}
      className="pointer-events-none fixed inset-x-0 bottom-[calc(5rem+env(safe-area-inset-bottom))] z-50 mx-auto flex w-full max-w-md flex-col gap-2 px-4 md:bottom-6"
    >
      {items.map((item) => (
        <ToastMessage key={item.id} item={item} />
      ))}
    </div>
  );
}
