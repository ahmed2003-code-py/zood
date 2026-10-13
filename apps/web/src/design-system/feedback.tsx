import { Inbox, RefreshCw, TriangleAlert } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/cn';
import { Button } from './Button';
import { Icon, type LucideIcon } from './Icon';
import { TONE_ICON, type Tone } from './tone';

const BANNER_TONE: Record<Exclude<Tone, 'neutral' | 'brand'>, string> = {
  success: 'border-success/30 bg-success-subtle [&_[data-banner-icon]]:text-success',
  warning: 'border-warning/30 bg-warning-subtle [&_[data-banner-icon]]:text-warning',
  danger: 'border-danger/30 bg-danger-subtle [&_[data-banner-icon]]:text-danger',
  info: 'border-info/30 bg-info-subtle [&_[data-banner-icon]]:text-info',
};

interface BannerProps {
  tone: keyof typeof BANNER_TONE;
  title: string;
  description?: string;
  action?: ReactNode;
  className?: string;
}

export function Banner({ tone, title, description, action, className }: BannerProps) {
  const glyph = TONE_ICON[tone];
  return (
    <div
      role={tone === 'danger' || tone === 'warning' ? 'alert' : 'status'}
      className={cn(
        'flex flex-col gap-3 rounded-container border p-4 text-foreground sm:flex-row sm:items-center',
        BANNER_TONE[tone],
        className,
      )}
    >
      <div className="flex min-w-0 flex-1 gap-3">
        {glyph && (
          <span data-banner-icon className="mt-0.5">
            <Icon icon={glyph} />
          </span>
        )}
        <div className="min-w-0">
          <p className="font-semibold">{title}</p>
          {description && <p className="mt-0.5 text-sm text-muted">{description}</p>}
        </div>
      </div>
      {action && <div className="shrink-0 ps-8 sm:ps-0">{action}</div>}
    </div>
  );
}

interface EmptyStateProps {
  title?: string;
  description?: string;
  icon?: LucideIcon;
  action?: ReactNode;
  className?: string;
}

export function EmptyState({ title, description, icon = Inbox, action, className }: EmptyStateProps) {
  const { t } = useTranslation('errors');
  return (
    <div className={cn('flex flex-col items-center gap-2 px-6 py-10 text-center', className)}>
      <span className="mb-1 flex size-12 items-center justify-center rounded-full bg-surface-sunken text-muted">
        <Icon icon={icon} size={22} />
      </span>
      <p className="font-semibold">{title ?? t('empty.title')}</p>
      <p className="max-w-sm text-sm text-muted">{description ?? t('empty.description')}</p>
      {action && <div className="mt-2">{action}</div>}
    </div>
  );
}

export function ErrorState({ onRetry, className }: { onRetry?: () => void; className?: string }) {
  const { t } = useTranslation(['errors', 'common']);
  return (
    <EmptyState
      icon={TriangleAlert}
      title={t('errors:generic.title')}
      description={t('errors:generic.description')}
      className={className}
      action={
        onRetry && (
          <Button variant="secondary" leadingIcon={RefreshCw} onClick={onRetry}>
            {t('common:actions.retry')}
          </Button>
        )
      }
    />
  );
}

export function Skeleton({ className }: { className?: string }) {
  return <span aria-hidden className={cn('block animate-pulse rounded-md bg-surface-sunken', className)} />;
}
