import { SlidersHorizontal } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/cn';
import { Button } from './Button';
import { Sheet } from './Sheet';

interface FilterBarProps {
  children?: ReactNode;
  /** Always in view, also on phones: usually the search box. */
  primary?: ReactNode;
  /**
   * How many of the folded filters differ from their default. Passing it (even 0) folds `children`
   * into a "Filters" sheet on phones, with this count on its button.
   */
  activeCount?: number;
  /** Puts the folded filters back to their defaults (shown in the sheet when any is on). */
  onReset?: () => void;
  className?: string;
}

const BAR = 'border-b border-border bg-surface-muted px-4 py-3 md:px-5 md:py-4';

/**
 * Filters for the list right under it, drawn as the top band of the list's card. From 768px they sit
 * in one row and wrap. On phones they either stack (the default) or, when `activeCount` is given,
 * fold into a "Filters" sheet so the list starts near the top of the screen (U1 audit #7).
 */
export function FilterBar({ children, primary, activeCount, onReset, className }: FilterBarProps) {
  const { t } = useTranslation('common');
  const [open, setOpen] = useState(false);
  const foldable = activeCount !== undefined && children !== undefined;

  if (!foldable) {
    return (
      <div className={cn(BAR, 'flex flex-col gap-3 md:flex-row md:flex-wrap md:items-end', className)}>
        {primary}
        {children}
      </div>
    );
  }

  return (
    <>
      <div className={cn(BAR, 'hidden md:flex md:flex-row md:flex-wrap md:items-end md:gap-3', className)}>
        {primary}
        {children}
      </div>
      <div className={cn(BAR, 'flex items-end gap-2 md:hidden', className)}>
        {primary && <div className="min-w-0 flex-1">{primary}</div>}
        <Button
          variant="secondary"
          leadingIcon={SlidersHorizontal}
          className={cn('relative shrink-0', !primary && 'w-full')}
          aria-label={activeCount > 0 ? `${t('filters.open')}, ${t('filters.active', { count: activeCount })}` : t('filters.open')}
          onClick={() => setOpen(true)}
        >
          {t('filters.open')}
          {activeCount > 0 && (
            <span aria-hidden className="flex min-w-5 items-center justify-center rounded-full bg-primary px-1.5 text-xs leading-5 font-semibold text-primary-foreground tabular-nums">
              {activeCount}
            </span>
          )}
        </Button>
      </div>
      <Sheet
        open={open}
        onOpenChange={setOpen}
        title={t('filters.title')}
        description={activeCount > 0 ? t('filters.active', { count: activeCount }) : undefined}
        size="sm"
        footer={
          <>
            {onReset && activeCount > 0 && (
              <Button variant="ghost" onClick={onReset}>
                {t('filters.reset')}
              </Button>
            )}
            <Button onClick={() => setOpen(false)}>{t('filters.show')}</Button>
          </>
        }
      >
        <div className="flex flex-col gap-5 [&_[role=radiogroup]]:flex-wrap">{children}</div>
      </Sheet>
    </>
  );
}
