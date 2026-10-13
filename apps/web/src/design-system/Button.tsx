import { LoaderCircle } from 'lucide-react';
import { Slot } from 'radix-ui';
import type { ComponentProps } from 'react';
import { cn } from '@/lib/cn';
import { Icon, type LucideIcon } from './Icon';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export type ButtonSize = 'sm' | 'md' | 'lg' | 'pos';

const VARIANT: Record<ButtonVariant, string> = {
  primary: 'bg-primary text-primary-foreground hover:bg-primary-hover',
  secondary: 'border border-border-strong bg-surface text-foreground hover:bg-surface-sunken',
  ghost: 'text-foreground hover:bg-surface-sunken',
  danger: 'bg-danger text-danger-foreground hover:opacity-90',
};

// md and above meet the 44px touch target; sm is for dense desktop toolbars and grows on touch screens.
const SIZE: Record<ButtonSize, string> = {
  sm: 'min-h-9 px-3 text-sm pointer-coarse:min-h-11',
  md: 'min-h-11 px-4 text-[0.9375rem]',
  lg: 'min-h-12 px-5 text-base',
  pos: 'min-h-(--pos-button-h) px-6 text-xl',
};

export interface ButtonProps extends ComponentProps<'button'> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  leadingIcon?: LucideIcon;
  trailingIcon?: LucideIcon;
  /** Mirror the trailing icon in RTL (e.g. a "next" chevron). */
  trailingIconDirectional?: boolean;
  loading?: boolean;
  fullWidth?: boolean;
  /** Render the single child (e.g. a router Link) with button styling instead of a <button>. */
  asChild?: boolean;
}

export function Button({
  variant = 'primary',
  size = 'md',
  leadingIcon,
  trailingIcon,
  trailingIconDirectional = false,
  loading = false,
  fullWidth = false,
  asChild = false,
  disabled,
  className,
  children,
  type = 'button',
  ...rest
}: ButtonProps) {
  const iconSize = size === 'pos' ? 24 : size === 'sm' ? 16 : 18;
  const classes = cn(
    'inline-flex items-center justify-center gap-2 rounded-control font-semibold whitespace-nowrap transition-colors',
    'disabled:pointer-events-none disabled:opacity-50',
    VARIANT[variant],
    SIZE[size],
    fullWidth && 'w-full',
    className,
  );
  if (asChild) {
    return (
      <Slot.Root className={classes} {...rest}>
        {children}
      </Slot.Root>
    );
  }
  return (
    <button
      type={type}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={classes}
      {...rest}
    >
      {loading ? (
        <Icon icon={LoaderCircle} size={iconSize} className="animate-spin" />
      ) : (
        leadingIcon && <Icon icon={leadingIcon} size={iconSize} />
      )}
      {children}
      {trailingIcon && !loading && <Icon icon={trailingIcon} size={iconSize} directional={trailingIconDirectional} />}
    </button>
  );
}

interface IconButtonProps extends Omit<ComponentProps<'button'>, 'children'> {
  icon: LucideIcon;
  /** Accessible name (translated). Required because the button has no visible text. */
  label: string;
  variant?: Exclude<ButtonVariant, 'danger'>;
  directional?: boolean;
}

export function IconButton({
  icon,
  label,
  variant = 'ghost',
  directional = false,
  className,
  type = 'button',
  ...rest
}: IconButtonProps) {
  return (
    <button
      type={type}
      aria-label={label}
      title={label}
      className={cn(
        'inline-flex size-11 shrink-0 items-center justify-center rounded-control transition-colors',
        'disabled:pointer-events-none disabled:opacity-50',
        VARIANT[variant],
        className,
      )}
      {...rest}
    >
      <Icon icon={icon} directional={directional} />
    </button>
  );
}
