/**
 * Who did a step of a maker-checker record (requested, verified, decided). A person is shown by
 * name, never by id (QA round 1: "Verified by 01a1f8dd"). The API sends a display name and a role
 * next to each actor id (`verifiedByName`, `verifiedByRole`, …); a company or station reads ZAWED
 * staff as the role ZAWED_OPERATIONS with no name.
 */
import { useTranslation } from 'react-i18next';
import { useSession } from '@/auth/session-store';

export type ActorField = 'requestedBy' | 'verifiedBy' | 'approvedBy' | 'decidedBy';

/** The display name the API sent for an actor field, if any. */
export function actorDisplayName(record: object, field: ActorField): string | undefined {
  const value = (record as Record<string, unknown>)[`${field}Name`];
  return typeof value === 'string' && value.trim() !== '' ? value.trim() : undefined;
}

/** The actor id on the record, if any. */
function actorId(record: object, field: ActorField): string | undefined {
  const value = (record as Record<string, unknown>)[field];
  return typeof value === 'string' && value !== '' ? value : undefined;
}

/** The role the API sent for an actor field (`verifiedByRole`, …), if any. */
function actorRole(record: object, field: ActorField): string | undefined {
  const value = (record as Record<string, unknown>)[`${field}Role`];
  return typeof value === 'string' && value !== '' ? value : undefined;
}

/**
 * The name for an actor field: "you" when it is the signed-in user, the API's display name, "ZAWED
 * operations" when the API hides staff names from this portal, the person's role, or "a colleague".
 * Undefined when nobody has done that step yet.
 */
export function useActorName(): (record: object, field: ActorField) => string | undefined {
  const { t } = useTranslation(['portal', 'live']);
  const userId = useSession((state) => state.user?.id);
  return (record, field) => {
    const id = actorId(record, field);
    const name = actorDisplayName(record, field);
    const role = actorRole(record, field);
    if (!id && !name && !role) return undefined;
    if (id && id === userId) return t('portal:actor.you');
    if (name) return name;
    if (role === 'ZAWED_OPERATIONS') return t('portal:actor.zawedOperations');
    if (role) return t(`live:enums.userRole.${role}` as 'live:enums.userRole.OPS', { defaultValue: t('portal:actor.colleague') });
    return t('portal:actor.colleague');
  };
}

/** True when the signed-in user did this step (so they cannot do the next one). */
export function useIsOwnStep(): (record: object, field: ActorField) => boolean {
  const userId = useSession((state) => state.user?.id);
  return (record, field) => Boolean(userId) && actorId(record, field) === userId;
}

/**
 * A receiving account in words: the API's label when the record carries one (`receivingAccountLabel`),
 * else the web's name for a known code (MAIN), else the code itself.
 */
export function useAccountLabel(): (code: string, apiLabel?: string | null) => string {
  const { t } = useTranslation('portal');
  return (code, apiLabel) => (apiLabel && apiLabel.trim() ? apiLabel.trim() : t(`accounts.${code}` as 'accounts.MAIN', { defaultValue: code }));
}
