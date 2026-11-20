import { cn } from '@/lib/cn';

/**
 * The look every text control shares (inputs, selects, text areas): a quiet border that darkens on
 * hover, and on focus a brand border with a soft halo. Errors switch both to the danger colour.
 */
export function fieldControlClass(error?: boolean) {
  return cn(
    'w-full rounded-control border bg-input text-[0.9375rem] text-foreground shadow-control placeholder:text-muted',
    'transition-[border-color,box-shadow] duration-150 focus-visible:outline-none focus-visible:ring-3',
    'disabled:cursor-not-allowed disabled:bg-surface-sunken disabled:opacity-70',
    error
      ? 'border-danger focus-visible:border-danger focus-visible:ring-danger/20'
      : 'border-input-border hover:border-border-strong focus-visible:border-focus-ring focus-visible:ring-focus-ring/20',
  );
}
