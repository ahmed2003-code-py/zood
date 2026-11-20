import { ChevronDown, CircleAlert, Delete, FileText, Upload } from 'lucide-react';
import { ToggleGroup } from 'radix-ui';
import { useId, useRef, type InputHTMLAttributes, type ReactNode, type TextareaHTMLAttributes } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/cn';
import { fieldControlClass } from './field-styles';
import { Icon, type LucideIcon } from './Icon';

function FieldLabel({ htmlFor, children }: { htmlFor: string; children: ReactNode }) {
  return (
    <label htmlFor={htmlFor} className="text-label font-medium text-foreground">
      {children}
    </label>
  );
}

function FieldMessage({ id, hint, error }: { id: string; hint?: string; error?: string }) {
  if (error) {
    return (
      <p id={`${id}-error`} className="flex items-start gap-1.5 text-xs font-medium text-danger">
        <Icon icon={CircleAlert} size={14} className="mt-px" />
        {error}
      </p>
    );
  }
  if (!hint) return null;
  return (
    <p id={`${id}-hint`} className="text-xs text-pretty text-muted">
      {hint}
    </p>
  );
}

function describedBy(id: string, hint?: string, error?: string) {
  return [hint && !error && `${id}-hint`, error && `${id}-error`].filter(Boolean).join(' ') || undefined;
}

interface TextFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id'> {
  label: string;
  hint?: string;
  error?: string;
  leadingIcon?: LucideIcon;
  /** A control at the inline end of the input, e.g. a show-password button. */
  trailing?: ReactNode;
}

export function TextField({ label, hint, error, leadingIcon, trailing, className, ...inputProps }: TextFieldProps) {
  const id = useId();
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <FieldLabel htmlFor={id}>{label}</FieldLabel>
      {/* A field with its own direction (an email in an Arabic page) places its icons by that direction,
          so the text never runs under them. */}
      <div className="relative" dir={inputProps.dir}>
        {leadingIcon && (
          <span className="pointer-events-none absolute inset-y-0 start-3 flex items-center text-muted">
            <Icon icon={leadingIcon} size={18} />
          </span>
        )}
        <input
          id={id}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy(id, hint, error)}
          className={cn(
            fieldControlClass(Boolean(error)),
            'min-h-10 px-3 pointer-coarse:min-h-11',
            leadingIcon && 'ps-10',
            trailing && 'pe-12',
          )}
          {...inputProps}
        />
        {trailing && <span className="absolute inset-y-0 end-1 flex items-center">{trailing}</span>}
      </div>
      <FieldMessage id={id} hint={hint} error={error} />
    </div>
  );
}

interface TextAreaFieldProps extends Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, 'id'> {
  label: string;
  hint?: string;
  error?: string;
}

/** A labelled multi-line text field (messages, notes, reasons). */
export function TextAreaField({ label, hint, error, className, rows = 4, ...props }: TextAreaFieldProps) {
  const id = useId();
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <FieldLabel htmlFor={id}>{label}</FieldLabel>
      <textarea
        id={id}
        rows={rows}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(id, hint, error)}
        className={cn(fieldControlClass(Boolean(error)), 'min-h-24 resize-y px-3 py-2.5 leading-relaxed')}
        {...props}
      />
      <FieldMessage id={id} hint={hint} error={error} />
    </div>
  );
}

interface FileFieldProps {
  label: string;
  hint?: string;
  error?: string;
  accept?: string;
  file?: File;
  onFileChange: (file: File | undefined) => void;
  disabled?: boolean;
  className?: string;
}

/**
 * A file picker in the page's language. The browser's own control shows its text in the browser's
 * language ("Choose File"), so the real input is visually hidden behind a translated button.
 */
export function FileField({ label, hint, error, accept, file, onFileChange, disabled, className }: FileFieldProps) {
  const { t } = useTranslation();
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <FieldLabel htmlFor={id}>{label}</FieldLabel>
      <div
        className={cn(
          'flex flex-wrap items-center gap-3 rounded-control border border-dashed bg-surface-muted p-3',
          error ? 'border-danger' : 'border-input-border',
        )}
      >
        <span className="flex size-10 shrink-0 items-center justify-center rounded-control bg-primary-subtle text-primary-subtle-foreground">
          <Icon icon={file ? FileText : Upload} size={18} />
        </span>
        <span className={cn('min-w-0 flex-1 truncate text-sm', file ? 'font-medium' : 'text-muted')} dir={file ? 'auto' : undefined}>
          {file ? file.name : t('forms.noFile')}
        </span>
        <input
          ref={input}
          id={id}
          type="file"
          accept={accept}
          disabled={disabled}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy(id, hint, error)}
          tabIndex={-1}
          onChange={(event) => onFileChange(event.target.files?.[0])}
          className="sr-only"
        />
        <button
          type="button"
          disabled={disabled}
          onClick={() => input.current?.click()}
          className={cn(
            'inline-flex min-h-9 items-center rounded-control border border-border-strong/45 bg-surface px-3 text-sm font-semibold shadow-control',
            'transition-colors duration-150 hover:bg-surface-hover disabled:pointer-events-none disabled:opacity-50',
          )}
        >
          {t(file ? 'forms.replaceFile' : 'forms.chooseFile')}
        </button>
      </div>
      <FieldMessage id={id} hint={hint} error={error} />
    </div>
  );
}

interface SelectFieldProps<T extends string> {
  label: string;
  value: T;
  onValueChange: (value: T) => void;
  options: Array<{ value: T; label: string }>;
  hint?: string;
  error?: string;
  disabled?: boolean;
  className?: string;
}

/**
 * A labelled choice from a list, for forms with more options than a segmented control holds.
 * A native select: it opens the platform's own picker on phones and follows the page direction.
 */
export function SelectField<T extends string>({ label, value, onValueChange, options, hint, error, disabled, className }: SelectFieldProps<T>) {
  const id = useId();
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <FieldLabel htmlFor={id}>{label}</FieldLabel>
      <NativeSelect
        id={id}
        value={value}
        disabled={disabled}
        error={Boolean(error)}
        describedBy={describedBy(id, hint, error)}
        onChange={(next) => onValueChange(next as T)}
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </NativeSelect>
      <FieldMessage id={id} hint={hint} error={error} />
    </div>
  );
}

/**
 * A native select with the shared control look and its own chevron at the inline end. Exported for
 * the few filters that place their own label.
 */
export function NativeSelect({
  id,
  value,
  onChange,
  disabled,
  error,
  describedBy: described,
  className,
  children,
}: {
  id?: string;
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
  error?: boolean;
  describedBy?: string;
  className?: string;
  children: ReactNode;
}) {
  return (
    <span className={cn('relative block', className)}>
      <select
        id={id}
        value={value}
        disabled={disabled}
        aria-invalid={error ? true : undefined}
        aria-describedby={described}
        onChange={(event) => onChange(event.target.value)}
        className={cn(fieldControlClass(error), 'min-h-10 appearance-none ps-3 pe-10 pointer-coarse:min-h-11')}
      >
        {children}
      </select>
      <Icon icon={ChevronDown} size={16} className="pointer-events-none absolute end-3 top-1/2 -translate-y-1/2 text-muted" />
    </span>
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
      // Many options on a narrow screen scroll inside the control instead of widening the page.
      className={cn(
        'inline-flex max-w-full overflow-x-auto rounded-control bg-surface-sunken p-1 ring-1 ring-border ring-inset [scrollbar-width:none]',
        className,
      )}
    >
      {options.map((option) => (
        <ToggleGroup.Item
          key={option.value}
          value={option.value}
          lang={option.lang}
          className={cn(
            'inline-flex min-h-8 flex-1 shrink-0 items-center justify-center gap-1.5 rounded-[0.4375rem] px-3 text-sm font-medium whitespace-nowrap text-muted',
            'transition-[background-color,color,box-shadow] duration-150 pointer-coarse:min-h-9 hover:text-foreground',
            'data-[state=on]:bg-surface data-[state=on]:text-foreground data-[state=on]:shadow-control data-[state=on]:ring-1 data-[state=on]:ring-border',
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
