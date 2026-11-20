import { X } from 'lucide-react';
import { Dialog } from 'radix-ui';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/cn';
import { IconButton } from './Button';

/**
 * Width from 640px: sm for a reason or a confirmation, md for an ordinary form or a detail, lg for
 * a long form with two columns, xl for a table or a wide detail.
 */
export type SheetSize = 'sm' | 'md' | 'lg' | 'xl';

const WIDTH: Record<SheetSize, string> = {
  sm: 'sm:max-w-[26rem]',
  md: 'sm:max-w-[32rem]',
  lg: 'sm:max-w-[42rem]',
  xl: 'sm:max-w-[56rem]',
};

interface SheetProps {
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  /** Element that opens the dialog (rendered as-is via asChild). */
  trigger?: ReactNode;
  title: string;
  description?: string;
  children: ReactNode;
  footer?: ReactNode;
  size?: SheetSize;
}

/**
 * A focused task over the page: a centred dialog from 640px and a bottom sheet on phones.
 * The header and footer stay in place while the body scrolls. Focus is trapped inside, Escape and a
 * click on the overlay close it, and focus returns to the element that opened it (Radix Dialog).
 * Used for details, filters and short forms.
 */
export function Sheet({ open, onOpenChange, trigger, title, description, children, footer, size = 'md' }: SheetProps) {
  const { t } = useTranslation();
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      {trigger && <Dialog.Trigger asChild>{trigger}</Dialog.Trigger>}
      <Dialog.Portal>
        <Dialog.Overlay
          className={cn(
            'fixed inset-0 z-40 bg-overlay',
            'data-[state=open]:animate-[zood-fade-in_150ms_ease-out] data-[state=closed]:animate-[zood-fade-out_120ms_ease-in]',
          )}
        />
        {/* Centres the dialog without transforms, so the position is the same in RTL and LTR.
            It lets clicks through to the overlay, which closes the dialog. */}
        <div className="pointer-events-none fixed inset-0 z-50 flex items-end justify-center sm:items-center sm:p-6">
          <Dialog.Content
            className={cn(
              'pointer-events-auto flex w-full flex-col overflow-hidden bg-surface-elevated text-foreground shadow-dialog focus:outline-none',
              // Phones: a sheet that rises from the bottom edge.
              'max-h-[92dvh] rounded-t-dialog',
              'data-[state=open]:animate-[zood-sheet-from-bottom_240ms_cubic-bezier(0.22,1,0.36,1)] data-[state=closed]:animate-[zood-sheet-to-bottom_160ms_ease-in]',
              // From 640px: a centred dialog that scales in slightly.
              'sm:max-h-[min(88dvh,56rem)] sm:rounded-dialog',
              'sm:data-[state=open]:animate-[zood-dialog-in_180ms_cubic-bezier(0.22,1,0.36,1)] sm:data-[state=closed]:animate-[zood-fade-out_120ms_ease-in]',
              WIDTH[size],
            )}
          >
            <div aria-hidden className="mx-auto mt-2 h-1 w-10 shrink-0 rounded-full bg-border-strong sm:hidden" />
            <header className="flex shrink-0 items-start justify-between gap-4 border-b border-border px-5 pt-3 pb-4 sm:px-6 sm:pt-5">
              <div className="min-w-0 pt-1">
                <Dialog.Title className="text-title font-semibold text-balance">{title}</Dialog.Title>
                {description ? (
                  <Dialog.Description className="mt-1 text-sm text-pretty text-muted">{description}</Dialog.Description>
                ) : (
                  <Dialog.Description className="sr-only">{title}</Dialog.Description>
                )}
              </div>
              <Dialog.Close asChild>
                <IconButton icon={X} label={t('a11y.closeDialog')} className="-me-2 size-10 text-muted hover:text-foreground" />
              </Dialog.Close>
            </header>
            <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-5 py-5 sm:px-6">{children}</div>
            {footer && (
              <footer className="flex shrink-0 flex-col-reverse gap-2 border-t border-border bg-surface-muted px-5 py-4 pb-[max(1rem,env(safe-area-inset-bottom))] sm:flex-row sm:justify-end sm:px-6 sm:pb-4">
                {footer}
              </footer>
            )}
          </Dialog.Content>
        </div>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
