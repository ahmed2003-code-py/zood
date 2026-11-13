import { KeyRound, LockOpen, Pause, ShieldOff, Sun, UserX } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, DateTimeText, Sheet, toast } from '@/design-system';
import { DetailList } from '@/shell/page';
import { adminApi, type AdminUser, type TemporaryPassword, type UserStatus } from '../api';
import { UserStatusChip } from '../chips';
import { OneTimeSecret, ReasonSheet } from '../components';
import { SecretFlowSheet } from './secret-sheet';

/** Left-to-right text inside a translated sentence, isolated so it never reorders the sentence. */
const isolate = (value: string) => `⁨${value}⁩`;

type UserAction = { kind: 'resetPassword' } | { kind: 'status'; to: UserStatus } | { kind: 'mfa' };

/** A user's detail sheet and the follow-up dialogs, limited to what the signed-in role may do. */
export function UserSheets({
  user,
  organisation,
  isSuperAdmin,
  onClose,
  onChanged,
}: {
  user: AdminUser;
  organisation: ReactNode;
  isSuperAdmin: boolean;
  onClose: () => void;
  onChanged: () => void;
}) {
  const { t } = useTranslation('live');
  const [action, setAction] = useState<UserAction>();
  const [now] = useState(() => Date.now());
  const active = user.status !== 'DEACTIVATED';
  const locked = Boolean(user.lockedUntil) && new Date(user.lockedUntil as string).getTime() > now;
  const usesMfa = user.role === 'OPS' || user.role === 'SUPER_ADMIN';

  const unlock = async () => {
    try {
      await adminApi.users.unlock(user.id);
      toast(t('users.unlocked'));
      onChanged();
      onClose();
    } catch {
      toast(t('errors.generic'), 'danger');
    }
  };

  return (
    <>
      <Sheet
        open={!action}
        onOpenChange={(next) => {
          if (!next) onClose();
        }}
        title={user.fullName}
        description={t(`enums.userRole.${user.role}`)}
        footer={<Button onClick={onClose}>{t('users.close')}</Button>}
      >
        <div className="flex flex-col gap-4">
          <div className="overflow-hidden rounded-container border border-border bg-surface">
            <DetailList
              rows={[
                { label: t('users.columns.email'), value: <span dir="ltr">{user.email}</span> },
                { label: t('users.columns.status'), value: <UserStatusChip status={user.status} /> },
                { label: t('users.columns.organisation'), value: organisation },
                { label: t('users.mfa'), value: t(user.mfaEnabled ? 'users.mfaOn' : 'users.mfaOff') },
                user.mustChangePassword && { label: t('users.password'), value: t('users.mustChange') },
                locked && { label: t('users.locked'), value: <DateTimeText value={user.lockedUntil as string} /> },
                { label: t('common.createdAt'), value: <DateTimeText value={user.createdAt} variant="date" /> },
              ]}
            />
          </div>
          {active && (
            <div className="flex flex-col gap-2">
              <Button variant="secondary" leadingIcon={KeyRound} onClick={() => setAction({ kind: 'resetPassword' })}>
                {t('users.resetPassword')}
              </Button>
              {locked && (
                <Button variant="secondary" leadingIcon={LockOpen} onClick={unlock}>
                  {t('users.unlock')}
                </Button>
              )}
              {isSuperAdmin && usesMfa && (
                <Button variant="secondary" leadingIcon={ShieldOff} onClick={() => setAction({ kind: 'mfa' })}>
                  {t('users.resetMfa')}
                </Button>
              )}
            </div>
          )}
          {isSuperAdmin && active && (
            <div className="flex flex-col gap-2">
              {user.status === 'ACTIVE' ? (
                <Button variant="secondary" leadingIcon={Pause} onClick={() => setAction({ kind: 'status', to: 'SUSPENDED' })}>
                  {t('users.suspend')}
                </Button>
              ) : (
                <Button variant="secondary" leadingIcon={Sun} onClick={() => setAction({ kind: 'status', to: 'ACTIVE' })}>
                  {t('users.reactivate')}
                </Button>
              )}
              <Button variant="secondary" leadingIcon={UserX} onClick={() => setAction({ kind: 'status', to: 'DEACTIVATED' })}>
                {t('users.deactivate')}
              </Button>
            </div>
          )}
        </div>
      </Sheet>

      {action?.kind === 'resetPassword' && (
        <SecretFlowSheet<TemporaryPassword>
          title={t('users.resetPasswordTitle', { name: user.fullName })}
          description={t('users.resetPasswordHint')}
          submitLabel={t('users.resetPassword')}
          resultTitle={t('users.resetPasswordDone')}
          doneLabel={t('users.done')}
          form={<p className="text-sm">{t('users.resetPasswordConfirm', { email: isolate(user.email) })}</p>}
          run={() => adminApi.users.resetPassword(user.id)}
          renderResult={(result) => (
            <div className="flex flex-col gap-4">
              <OneTimeSecret label={t('userForm.temporaryPassword')} value={result.temporaryPassword} group={4} />
              <p className="text-sm text-muted">{t('userForm.mustChangeHint')}</p>
            </div>
          )}
          onChanged={onChanged}
          onClose={onClose}
        />
      )}
      {action?.kind === 'status' && (
        <ReasonSheet
          open
          onOpenChange={onClose}
          title={t(
            action.to === 'SUSPENDED' ? 'users.suspendTitle' : action.to === 'ACTIVE' ? 'users.reactivateTitle' : 'users.deactivateTitle',
            { name: user.fullName },
          )}
          description={t(action.to === 'SUSPENDED' ? 'users.suspendHint' : action.to === 'ACTIVE' ? 'users.reactivateHint' : 'users.deactivateHint')}
          submitLabel={t(action.to === 'SUSPENDED' ? 'users.suspend' : action.to === 'ACTIVE' ? 'users.reactivate' : 'users.deactivate')}
          submitVariant={action.to === 'ACTIVE' ? 'primary' : 'danger'}
          onConfirm={async (reason) => {
            await adminApi.users.setStatus(user.id, { status: action.to, reason });
            toast(t('users.statusChanged'));
            onChanged();
          }}
        />
      )}
      {action?.kind === 'mfa' && (
        <ReasonSheet
          open
          onOpenChange={onClose}
          title={t('users.resetMfaTitle', { name: user.fullName })}
          description={t('users.resetMfaHint')}
          submitLabel={t('users.resetMfa')}
          submitVariant="danger"
          onConfirm={async (reason) => {
            await adminApi.users.resetMfa(user.id, reason);
            toast(t('users.mfaReset'));
            onChanged();
          }}
        />
      )}
    </>
  );
}
