import { ChevronRight, Hourglass, Info } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, NavLink } from 'react-router';
import { Icon } from '@/design-system';
import { cn } from '@/lib/cn';

interface PageHeaderProps {
  title: string;
  description?: string;
  actions?: ReactNode;
}

export function PageHeader({ title, description, actions }: PageHeaderProps) {
  return (
    <div className="mb-6 flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
      <div className="min-w-0">
        <h1 className="text-xl font-semibold md:text-2xl">{title}</h1>
        {description && <p className="mt-1 max-w-2xl text-sm text-muted">{description}</p>}
      </div>
      {actions && <div className="flex shrink-0 flex-wrap gap-2">{actions}</div>}
    </div>
  );
}

/** A titled section placed directly on the page (no card wrapper). */
export function PageSection({
  title,
  action,
  children,
  className,
}: {
  title?: string;
  action?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={cn('flex min-w-0 flex-col gap-3', className)}>
      {(title || action) && (
        <div className="flex min-h-9 items-center justify-between gap-3">
          {title && <h2 className="text-base font-semibold">{title}</h2>}
          {action}
        </div>
      )}
      {children}
    </section>
  );
}

/** "View all" link for section headers. */
export function ViewAllLink({ to }: { to: string }) {
  const { t } = useTranslation();
  return (
    <Link to={to} className="flex min-h-9 items-center gap-1 rounded-control px-2 text-sm font-medium text-primary transition-colors hover:bg-surface-sunken">
      {t('actions.viewAll')}
      <Icon icon={ChevronRight} directional size={16} />
    </Link>
  );
}

/** Honest marker for screens whose full experience arrives in a later demo phase. */
export function PhaseNote() {
  const { t } = useTranslation('shell');
  return (
    <p className="flex items-center gap-2 rounded-control border border-dashed border-border-strong px-4 py-3 text-sm text-muted">
      <Icon icon={Hourglass} size={16} />
      {t('phaseNote')}
    </p>
  );
}

/** Bordered container for a list placed in a page section. */
export function ListPanel({ children }: { children: ReactNode }) {
  return <div className="overflow-hidden rounded-container border border-border bg-surface">{children}</div>;
}

/** Reminder that company data is view-only and changes go through requests (PRD §3). */
export function ViewOnlyNote() {
  const { t } = useTranslation('shell');
  return (
    <p className="flex items-start gap-2 text-sm text-muted">
      <Icon icon={Info} size={16} className="mt-0.5" />
      {t('viewOnly')}
    </p>
  );
}

/** Sub-navigation between sibling screens on small screens (the sidebar covers them on desktop). */
export function NavTabs({ items, label }: { items: Array<{ to: string; label: string }>; label: string }) {
  return (
    <nav aria-label={label} className="-mt-2 mb-5 flex gap-1 overflow-x-auto border-b border-border lg:hidden">
      {items.map((item) => (
        <NavLink
          key={item.to}
          to={item.to}
          className={({ isActive }) =>
            cn(
              '-mb-px flex min-h-11 items-center border-b-2 px-3 text-sm font-medium whitespace-nowrap transition-colors',
              isActive ? 'border-primary text-foreground' : 'border-transparent text-muted hover:text-foreground',
            )
          }
        >
          {item.label}
        </NavLink>
      ))}
    </nav>
  );
}

/** Label/value rows for details screens. Values align to the inline end. */
export function DetailList({ rows }: { rows: Array<{ label: string; value: ReactNode } | false | null | undefined | ''> }) {
  return (
    <dl className="divide-y divide-border">
      {rows.filter((row): row is { label: string; value: ReactNode } => Boolean(row)).map((row) => (
        <div key={row.label} className="flex min-h-12 items-center justify-between gap-4 px-4 py-2.5 text-sm">
          <dt className="shrink-0 text-muted">{row.label}</dt>
          <dd className="min-w-0 text-end font-medium">{row.value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** Progressive disclosure for secondary/technical details. */
export function Disclosure({ summary, children }: { summary: string; children: ReactNode }) {
  return (
    <details className="group overflow-hidden rounded-container border border-border bg-surface">
      <summary className="flex min-h-12 cursor-pointer list-none items-center justify-between gap-3 px-4 text-sm font-semibold [&::-webkit-details-marker]:hidden">
        {summary}
        <Icon icon={ChevronRight} size={18} className="text-muted transition-transform group-open:rotate-90 rtl:group-open:-rotate-90" directional />
      </summary>
      <div className="border-t border-border">{children}</div>
    </details>
  );
}

/** Back link at the top of a detail screen (mobile and desktop). */
export function BackLink({ to, label }: { to: string; label: string }) {
  return (
    <Link to={to} className="-ms-2 mb-3 inline-flex min-h-10 items-center gap-1.5 rounded-control px-2 text-sm font-medium text-muted transition-colors hover:text-foreground">
      <Icon icon={ChevronRight} size={16} directional className="rotate-180" />
      {label}
    </Link>
  );
}
