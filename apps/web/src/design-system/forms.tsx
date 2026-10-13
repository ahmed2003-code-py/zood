import { CircleAlert, Delete } from 'lucide-react';
import { ToggleGroup } from 'radix-ui';
import { useId, type InputHTMLAttributes, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/cn';
import { Icon, type LucideIcon } from './Icon';

interface TextFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id'> {
  label: string;
  hint?: string;
  error?: string;
  leadingIcon?: LucideIcon;
}

export function TextField({ label, hint, error, leadingIcon, className, ...inputProps }: TextFieldProps) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  const describedBy = [hint && hintId, error && errorId].filter(Boolean).join(' ') || undefined;
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <label htmlFor={id} className="text-sm font-medium">
        {label}
      </label>
      <div className="relative">
        {leadingIcon && (
          <span className="pointer-events-none absolute inset-y-0 start-3 flex items-center text-muted">
            <Icon icon={leadingIcon} size={18} />
          </span>
        )}
        <input
          id={id}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy}
          className={cn(
            'min-h-11 w-full rounded-control border bg-input px-3 text-foreground placeholder:text-muted',
            'transition-colors focus-visible:border-focus-ring',
            error ? 'border-danger' : 'border-input-border',
            leadingIcon && 'ps-10',
          )}
          {...inputProps}
        />
      </div>
      {hint && !error && (
        <p id={hintId} className="text-xs text-muted">
          {hint}
        </p>
      )}
      {error && (
        <p id={errorId} className="flex items-center gap-1 text-xs font-medium text-danger">
          <Icon icon={CircleAlert} size={14} />
          {error}
        </p>
      )}
    </div>
  );
}

export interface SegmentOption<T extends string> {
  value: T;
  label: ReactNode;
  icon?: LucideIcon;
  /** Language of the label when it differs from the page (e.g. "English" on an Arabic page). */
  lang?: string;
}

interface SegmentedControlProps<T extends string> {
  value: T;
  onValueChange: (value: T) => void;
  options: SegmentOption<T>[];
  /** Accessible name for the group (translated). */
  label: string;
  className?: string;
}

/** Single-choice segmented control (radio semantics). Arrow keys follow the reading direction. */
export function SegmentedControl<T extends string>({ value, onValueChange, options, label, className }: SegmentedControlProps<T>) {
  return (
    <ToggleGroup.Root
      type="single"
      value={value}
      aria-label={label}
      onValueChange={(next) => {
        // Ignore deselection: a segmented control always has one selected option.
        if (next) onValueChange(next as T);
      }}
      className={cn('inline-flex max-w-full rounded-control border border-border bg-surface-sunken p-1', className)}
    >
      {options.map((option) => (
        <ToggleGroup.Item
          key={option.value}
          value={option.value}
          lang={option.lang}
          className={cn(
            'inline-flex min-h-9 flex-1 items-center justify-center gap-1.5 rounded-[0.5rem] px-3 text-sm font-medium whitespace-nowrap text-muted transition-colors',
            'pointer-coarse:min-h-11 hover:text-foreground',
            'data-[state=on]:bg-surface data-[state=on]:text-foreground data-[state=on]:ring-1 data-[state=on]:ring-border',
          )}
        >
          {option.icon && <Icon icon={option.icon} size={16} />}
          {option.label}
        </ToggleGroup.Item>
      ))}
    </ToggleGroup.Root>
  );
}

interface NumpadProps {
  onDigit: (digit: string) => void;
  onDecimal?: () => void;
  onBackspace: () => void;
  className?: string;
}

const DIGITS = ['1', '2', '3', '4', '5', '6', '7', '8', '9'];

/**
 * POS numeric keypad. The key layout is always 1-2-3 left to right (like ATMs and payment
 * terminals in Arabic-speaking markets), so it is rendered LTR in both languages.
 */
export function Numpad({ onDigit, onDecimal, onBackspace, className }: NumpadProps) {
  const { t } = useTranslation();
  const key =
    'flex min-h-(--pos-key-h,3.5rem) items-center justify-center rounded-control border border-border bg-surface text-2xl font-semibold tabular-nums transition-colors active:bg-surface-sunken select-none';
  return (
    <div dir="ltr" className={cn('grid grid-cols-3 gap-2', className)}>
      {DIGITS.map((digit) => (
        <button key={digit} type="button" className={key} onClick={() => onDigit(digit)}>
          {digit}
        </button>
      ))}
      {onDecimal ? (
        <button type="button" className={key} onClick={onDecimal} aria-label={t('a11y.decimalPoint')}>
          .
        </button>
      ) : (
        <span />
      )}
      <button type="button" className={key} onClick={() => onDigit('0')}>
        0
      </button>
      <button type="button" className={key} onClick={onBackspace} aria-label={t('a11y.deleteDigit')}>
        <Icon icon={Delete} size={26} />
      </button>
    </div>
  );
}
