import { Check } from 'lucide-react';
import { Tabs as RadixTabs } from 'radix-ui';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/cn';
import { Icon } from './Icon';

interface TabItem {
  value: string;
  label: string;
  content: ReactNode;
}

interface TabsProps {
  items: TabItem[];
  defaultValue?: string;
  /** Accessible label for the tab list. */
  label: string;
}

/** Underlined tabs. Keyboard arrows follow the reading direction (via the Radix DirectionProvider). */
export function Tabs({ items, defaultValue, label }: TabsProps) {
  return (
    <RadixTabs.Root defaultValue={defaultValue ?? items[0]?.value}>
      <RadixTabs.List aria-label={label} className="flex gap-1 overflow-x-auto border-b border-border">
        {items.map((item) => (
          <RadixTabs.Trigger
            key={item.value}
            value={item.value}
            className={cn(
              '-mb-px min-h-11 border-b-2 border-transparent px-3 text-sm font-medium whitespace-nowrap text-muted transition-colors',
              'hover:text-foreground data-[state=active]:border-primary data-[state=active]:text-foreground',
            )}
          >
            {item.label}
          </RadixTabs.Trigger>
        ))}
      </RadixTabs.List>
      {items.map((item) => (
        <RadixTabs.Content key={item.value} value={item.value} className="pt-4 focus-visible:outline-none">
          {item.content}
        </RadixTabs.Content>
      ))}
    </RadixTabs.Root>
  );
}

interface StepperProps {
  /** Translated step labels. */
  steps: string[];
  /** Zero-based index of the current step. */
  current: number;
  className?: string;
}

/** Multi-step progress. Compact "Step 2 of 5" with a bar on mobile; full step list from 768px. */
export function Stepper({ steps, current, className }: StepperProps) {
  const { t } = useTranslation();
  const total = steps.length;
  const progress = Math.round(((current + 1) / total) * 100);
  return (
    <div className={className}>
      <div className="md:hidden">
        <div className="flex items-baseline justify-between gap-3 text-sm">
          <span className="font-semibold">{steps[current]}</span>
          <span className="text-muted">{t('stepper.progress', { current: current + 1, total })}</span>
        </div>
        <div
          className="mt-2 h-1.5 overflow-hidden rounded-full bg-surface-sunken"
          role="progressbar"
          aria-valuemin={1}
          aria-valuemax={total}
          aria-valuenow={current + 1}
          aria-valuetext={t('stepper.progress', { current: current + 1, total })}
        >
          <div className="h-full rounded-full bg-primary transition-[width]" style={{ width: `${progress}%` }} />
        </div>
      </div>
      <ol className="hidden items-center gap-2 md:flex">
        {steps.map((step, index) => {
          const state = index < current ? 'done' : index === current ? 'current' : 'upcoming';
          return (
            <li key={step} className="flex min-w-0 flex-1 items-center gap-2" aria-current={state === 'current' ? 'step' : undefined}>
              <span
                className={cn(
                  'flex size-7 shrink-0 items-center justify-center rounded-full border text-xs font-semibold tabular-nums',
                  state === 'done' && 'border-primary bg-primary text-primary-foreground',
                  state === 'current' && 'border-primary text-primary',
                  state === 'upcoming' && 'border-border-strong text-muted',
                )}
              >
                {state === 'done' ? <Icon icon={Check} size={14} /> : index + 1}
              </span>
              <span className={cn('truncate text-sm', state === 'upcoming' ? 'text-muted' : 'font-medium')}>{step}</span>
              {index < total - 1 && <span aria-hidden className="h-px min-w-4 flex-1 bg-border" />}
            </li>
          );
        })}
      </ol>
    </div>
  );
}
