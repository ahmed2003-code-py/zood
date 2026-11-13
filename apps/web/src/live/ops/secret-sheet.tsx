import { useState, type ReactNode } from 'react';
import { Button, Sheet } from '@/design-system';
import type { LiveError } from '../errors';
import { FormSheet } from '../components';

/**
 * A change whose answer contains a secret the server shows once (an activation code, a PIN). The
 * sheet first asks (or just confirms), then switches to the secret in the same sheet. The secret
 * lives only in this component's state, so closing the sheet (which unmounts it) forgets it.
 */
export function SecretFlowSheet<R>({
  title,
  description,
  submitLabel,
  submitVariant,
  resultTitle,
  doneLabel,
  form,
  run,
  onError,
  renderResult,
  onChanged,
  onClose,
}: {
  title: string;
  description?: string;
  submitLabel: string;
  submitVariant?: 'primary' | 'danger';
  resultTitle: string;
  doneLabel: string;
  form: ReactNode;
  /** Sends the change. Return false to stay on the form (local validation failed). */
  run: () => Promise<R | false>;
  onError?: (error: LiveError) => void;
  renderResult: (result: R) => ReactNode;
  /** Called as soon as the change succeeded, so the list behind refreshes without the secret. */
  onChanged: () => void;
  onClose: () => void;
}) {
  const [result, setResult] = useState<{ value: R }>();
  if (result) {
    return (
      <Sheet
        open
        onOpenChange={(next) => {
          if (!next) onClose();
        }}
        title={resultTitle}
        footer={<Button onClick={onClose}>{doneLabel}</Button>}
      >
        {renderResult(result.value)}
      </Sheet>
    );
  }
  return (
    <FormSheet
      open
      onOpenChange={(next) => {
        if (!next) onClose();
      }}
      title={title}
      description={description}
      submitLabel={submitLabel}
      submitVariant={submitVariant}
      onError={onError}
      onSubmit={async () => {
        const value = await run();
        if (value === false) return false;
        setResult({ value });
        onChanged();
        return false;
      }}
    >
      {form}
    </FormSheet>
  );
}
