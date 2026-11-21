import { LoaderCircle } from 'lucide-react';
import { Slot } from 'radix-ui';
import type { ComponentProps } from 'react';
import { cn } from '@/lib/cn';
import { Icon, type LucideIcon } from './Icon';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export type ButtonSize = 'sm' | 'md' | 'lg' | 'pos';

// A filled button that is disabled turns neutral instead of a faded brand colour, so it never reads
// as a different, softer action. A loading button keeps its colour (aria-busy).
const FILLED_DISABLED = 'disabled:not-aria-busy:bg-surface-sunken disabled:not-aria-busy:text-muted disabled:not-aria-busy:shadow-none';

const VARIANT: Record<ButtonVariant, string> = {
  primary: cn('bg-primary text-primary-foreground shadow-control hover:bg-primary-hover', FILLED_DISABLED),
  secondary:
    'border border-border-strong/45 bg-surface text-foreground shadow-control hover:border-border-strong/70 hover:bg-surface-hover disabled:opacity-50',
  ghost: 'text-foreground hover:bg-surface-sunken disabled:opacity-50',
  danger: cn('bg-danger text-danger-foreground shadow-control hover:bg-danger/90', FILLED_DISABLED),
};

// On touch screens every size meets the 44px target; with a mouse, sm and md are calmer (36 and 40px).
const SIZE: Record<ButtonSize, string> = {
  sm: 'min-h-9 px-3 text-sm pointer-coarse:min-h-11',
  md: 'min-h-10 px-4 text-[0.9375rem] pointer-coarse:min-h-11',
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
    'inline-flex items-center justify-center gap-2 rounded-control font-semibold whitespace-nowrap select-none',
    'transition-[background-color,border-color,color,box-shadow] duration-150',
    'disabled:pointer-events-none',
    // A small press: the button gives under the finger, without moving the layout.
    size !== 'pos' && 'active:translate-y-px',
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

const ICON_VARIANT: Record<Exclude<ButtonVariant, 'danger'>, string> = {
  primary: VARIANT.primary,
  secondary: VARIANT.secondary,
  ghost: VARIANT.ghost,
};

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
        'inline-flex size-10 shrink-0 items-center justify-center rounded-control transition-colors duration-150 pointer-coarse:size-11',
        'disabled:pointer-events-none disabled:opacity-50',
        ICON_VARIANT[variant],
        className,
      )}
      {...rest}
    >
      <Icon icon={icon} directional={directional} />
    </button>
  );
}
