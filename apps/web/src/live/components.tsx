/**
 * Building blocks shared by the production screens: list states, "load more", the one-time secret
 * display, and a form sheet that submits to the API and shows its refusal.
 */
import { Copy, KeyRound } from 'lucide-react';
import { useState, type FormEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Banner, Button, EmptyState, ErrorState, Icon, Sheet, Skeleton, TextField, toast } from '@/design-system';
import { liveError, type LiveError } from './errors';
import type { PagedResource } from './use-resource';

/** Loading, error and empty states around a paginated list, then the list and "load more". */
export function PagedList<T>({
  resource,
  empty,
  children,
}: {
  resource: PagedResource<T>;
  empty: ReactNode;
  children: (items: T[]) => ReactNode;
}) {
  const { t } = useTranslation('live');
  if (resource.loading) {
    return (
      <div className="flex flex-col gap-3 p-4" aria-busy>
        {Array.from({ length: 4 }, (_, index) => (
          <Skeleton key={index} className="h-10" />
        ))}
      </div>
    );
  }
  if (resource.error && resource.items.length === 0) return <ErrorState onRetry={resource.reload} />;
  if (resource.items.length === 0) return <>{empty}</>;
  return (
    <>
      {children(resource.items)}
      {(resource.hasMore || resource.error) && (
        <div className="flex justify-center border-t border-border p-3">
          {resource.error ? (
            <ErrorState onRetry={resource.loadMore} className="py-4" />
          ) : (
            <Button variant="ghost" loading={resource.loadingMore} onClick={resource.loadMore}>
              {t('common.loadMore')}
            </Button>
          )}
        </div>
      )}
    </>
  );
}

/** An empty list that explains itself and offers the next step. */
export function EmptyList({ title, description, action }: { title: string; description: string; action?: ReactNode }) {
  return <EmptyState title={title} description={description} action={action} />;
}

/**
 * A secret the server generated and will never show again (a device activation code, an agent
 * PIN). Read left to right in both languages, grouped for reading aloud, with a copy button.
 */
export function OneTimeSecret({ label, value, group = 4 }: { label: string; value: string; group?: number }) {
  const { t } = useTranslation('live');
  const grouped = value.match(new RegExp(`.{1,${group}}`, 'g'))?.join(' ') ?? value;
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      toast(t('common.copied'));
    } catch {
      toast(t('common.copyFailed'), 'danger');
    }
  };
  return (
    <div className="flex flex-col gap-3">
      <Banner tone="warning" title={t('common.secretOnce')} description={t('common.secretOnceHint')} />
      <div className="flex flex-col gap-1.5">
        <span className="text-sm font-medium">{label}</span>
        <div className="flex items-center gap-2 rounded-control border border-border-strong bg-surface-sunken p-3">
          <Icon icon={KeyRound} className="text-muted" />
          <span dir="ltr" className="min-w-0 flex-1 font-mono text-2xl font-semibold tracking-widest select-all">
            {grouped}
          </span>
          <Button variant="secondary" size="sm" leadingIcon={Copy} onClick={copy}>
            {t('common.copy')}
          </Button>
        </div>
      </div>
    </div>
  );
}

interface FormSheetProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  submitLabel: string;
  submitVariant?: 'primary' | 'danger';
  /** Validates and sends. Return false to stay open without an API call (local validation failed). */
  onSubmit: () => Promise<boolean | void>;
  /** Called with the API's refusal, so the form can mark the blamed field. */
  onError?: (error: LiveError) => void;
  children: ReactNode;
}

/**
 * A short form in a sheet. The submit button shows progress; an API refusal stays visible above
 * the fields in the user's language, and the sheet closes only when the change succeeded.
 */
export function FormSheet({
  open,
  onOpenChange,
  title,
  description,
  submitLabel,
  submitVariant = 'primary',
  onSubmit,
  onError,
  children,
}: FormSheetProps) {
  const { t } = useTranslation(['live', 'common']);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<LiveError>();
  const formId = `form-${title.replace(/\s+/g, '-')}`;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setFailure(undefined);
    try {
      const result = await onSubmit();
      if (result !== false) onOpenChange(false);
    } catch (error) {
      const described = liveError(t, error);
      setFailure(described);
      onError?.(described);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Sheet
      open={open}
      onOpenChange={(next) => {
        if (!next) setFailure(undefined);
        onOpenChange(next);
      }}
      title={title}
      description={description}
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            {t('common:actions.cancel')}
          </Button>
          <Button type="submit" form={formId} variant={submitVariant} loading={busy}>
            {submitLabel}
          </Button>
        </>
      }
    >
      <form id={formId} noValidate onSubmit={submit} className="flex flex-col gap-4">
        {failure && <Banner tone="danger" title={failure.message} description={failure.reference} />}
        {children}
      </form>
    </Sheet>
  );
}

/** A change that needs a stated reason (freeze, suspend, report stolen…). */
export function ReasonSheet({
  open,
  onOpenChange,
  title,
  description,
  submitLabel,
  submitVariant = 'primary',
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  submitLabel: string;
  submitVariant?: 'primary' | 'danger';
  onConfirm: (reason: string) => Promise<void>;
}) {
  const { t } = useTranslation('live');
  const [reason, setReason] = useState('');
  const [missing, setMissing] = useState(false);
  return (
    <FormSheet
      open={open}
      onOpenChange={(next) => {
        if (!next) {
          setReason('');
          setMissing(false);
        }
        onOpenChange(next);
      }}
      title={title}
      description={description}
      submitLabel={submitLabel}
      submitVariant={submitVariant}
      onSubmit={async () => {
        if (!reason.trim()) {
          setMissing(true);
          return false;
        }
        await onConfirm(reason.trim());
        setReason('');
      }}
    >
      <TextField
        label={t('common.reason')}
        hint={t('common.reasonHint')}
        value={reason}
        maxLength={500}
        error={missing ? t('errors.required') : undefined}
        onChange={(event) => {
          setReason(event.target.value);
          setMissing(false);
        }}
      />
    </FormSheet>
  );
}
