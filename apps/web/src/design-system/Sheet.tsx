import { X } from 'lucide-react';
import { Dialog } from 'radix-ui';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/cn';
import { IconButton } from './Button';

interface SheetProps {
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  /** Element that opens the sheet (rendered as-is via asChild). */
  trigger?: ReactNode;
  title: string;
  description?: string;
  children: ReactNode;
  footer?: ReactNode;
}

/**
 * Bottom sheet on mobile; side panel anchored to the inline-end edge from 768px.
 * Used for details, filters and short forms (progressive disclosure).
 */
export function Sheet({ open, onOpenChange, trigger, title, description, children, footer }: SheetProps) {
  const { t } = useTranslation();
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      {trigger && <Dialog.Trigger asChild>{trigger}</Dialog.Trigger>}
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-overlay animate-[zood-fade-in_150ms_ease-out]" />
        <Dialog.Content
          className={cn(
            'fixed z-50 flex flex-col bg-surface-elevated text-foreground shadow-elevated focus:outline-none',
            // Mobile: bottom sheet.
            'start-0 end-0 bottom-0 max-h-[88dvh] rounded-t-2xl animate-[zood-sheet-from-bottom_220ms_ease-out]',
            // Tablet and up: side panel at the inline end (right in LTR, left in RTL).
            'md:start-auto md:top-0 md:h-dvh md:max-h-none md:w-[28rem] md:rounded-none md:animate-[zood-sheet-from-end_220ms_ease-out]',
          )}
        >
          <div aria-hidden className="mx-auto mt-2 h-1 w-10 rounded-full bg-border-strong md:hidden" />
          <header className="flex items-start justify-between gap-3 px-5 pt-3 pb-3 md:pt-5">
            <div className="min-w-0">
              <Dialog.Title className="text-lg font-semibold">{title}</Dialog.Title>
              {description ? (
                <Dialog.Description className="mt-0.5 text-sm text-muted">{description}</Dialog.Description>
              ) : (
                <Dialog.Description className="sr-only">{title}</Dialog.Description>
              )}
            </div>
            <Dialog.Close asChild>
              <IconButton icon={X} label={t('a11y.closeDialog')} className="-me-2 -mt-1" />
            </Dialog.Close>
          </header>
          <div className="min-h-0 flex-1 overflow-y-auto px-5 pb-5">{children}</div>
          {footer && (
            <footer className="flex flex-col-reverse gap-2 border-t border-border px-5 py-4 pb-[max(1rem,env(safe-area-inset-bottom))] sm:flex-row sm:justify-end">
              {footer}
            </footer>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
