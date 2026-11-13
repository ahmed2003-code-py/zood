import type { ReactNode } from 'react';
import { BrandLogo, LanguageSwitcher, ThemeSwitcher } from '@/design-system';

/** The frame of every sign-in screen: brand, language and theme, one focused card. */
export function AuthLayout({ title, description, children }: { title: string; description?: string; children: ReactNode }) {
  return (
    <div className="flex min-h-dvh flex-col bg-background">
      <header className="flex flex-wrap items-center justify-between gap-3 px-4 py-3 sm:px-6">
        <BrandLogo size="sm" variant="compact" />
        <div className="flex flex-wrap items-center gap-2">
          <LanguageSwitcher />
          <ThemeSwitcher />
        </div>
      </header>
      <main className="flex flex-1 items-start justify-center px-4 pt-6 pb-12 sm:items-center sm:pt-0">
        <section className="w-full max-w-md rounded-container border border-border bg-surface p-6 shadow-elevated sm:p-8">
          <BrandLogo size="md" className="mb-6" />
          <h1 className="text-xl font-semibold">{title}</h1>
          {description && <p className="mt-1.5 text-sm text-muted">{description}</p>}
          <div className="mt-6">{children}</div>
        </section>
      </main>
    </div>
  );
}

/** A refusal shown above a form; announced to screen readers. */
export function FormError({ message, reference }: { message?: string; reference?: string }) {
  if (!message) return null;
  return (
    <div role="alert" className="rounded-control border border-danger bg-danger-subtle px-3 py-2.5 text-sm text-danger">
      <p className="font-medium">{message}</p>
      {reference && <p className="mt-0.5 text-xs">{reference}</p>}
    </div>
  );
}
