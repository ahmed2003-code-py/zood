import { Fuel } from 'lucide-react';
import { Button, Icon } from '@/design-system';

/**
 * The sign-in screens' main button. While the request runs it keeps its colour, says what is
 * happening, and fills from the reading edge like a tank (signin.css).
 */
export function SubmitButton({ busy, disabled, label, busyLabel }: { busy: boolean; disabled?: boolean; label: string; busyLabel: string }) {
  return (
    <Button type="submit" size="lg" fullWidth disabled={busy || disabled} aria-busy={busy || undefined} className="signin-submit">
      <span aria-hidden className="signin-submit-fill" />
      {busy && <Icon icon={Fuel} size={18} className="signin-submit-icon" />}
      <span>{busy ? busyLabel : label}</span>
    </Button>
  );
}
