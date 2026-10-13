import { cn } from '@/lib/cn';
import { Icon, type LucideIcon } from './Icon';
import { TONE_ICON, type Tone } from './tone';

const TONE_CLASS: Record<Tone, string> = {
  success: 'bg-success-subtle text-success',
  warning: 'bg-warning-subtle text-warning',
  danger: 'bg-danger-subtle text-danger',
  info: 'bg-info-subtle text-info',
  neutral: 'bg-surface-sunken text-muted',
  brand: 'bg-primary-subtle text-primary-subtle-foreground',
};

interface StatusChipProps {
  tone: Tone;
  /** Translated label. Status is always icon + text + color, never color alone. */
  label: string;
  icon?: LucideIcon;
  size?: 'sm' | 'md';
  className?: string;
}

export function StatusChip({ tone, label, icon, size = 'sm', className }: StatusChipProps) {
  const glyph = icon ?? TONE_ICON[tone];
  return (
    <span
      className={cn(
        'inline-flex max-w-full items-center gap-1 rounded-full font-medium whitespace-nowrap',
        size === 'sm' ? 'px-2 py-0.5 text-xs' : 'px-2.5 py-1 text-sm',
        TONE_CLASS[tone],
        className,
      )}
    >
      {glyph ? (
        <Icon icon={glyph} size={size === 'sm' ? 14 : 16} />
      ) : (
        <span aria-hidden className="size-1.5 rounded-full bg-current" />
      )}
      <span className="truncate">{label}</span>
    </span>
  );
}
