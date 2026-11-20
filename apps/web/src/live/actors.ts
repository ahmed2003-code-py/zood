/**
 * Who did a step of a maker-checker record (verified, requested, approved, decided). A person is
 * shown by name, never by id (QA round 1: "Verified by 01a1f8dd"). The API adds a display name next
 * to each actor id (`verifiedByName`, `requestedByName`, …); until a record carries one, the signed-in
 * user reads "you" for their own step and everyone else "a colleague".
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

/**
 * The name for an actor field: the API's display name, "you" when it is the signed-in user, or "a
 * colleague". Undefined when nobody has done that step yet.
 */
export function useActorName(): (record: object, field: ActorField) => string | undefined {
  const { t } = useTranslation('portal');
  const userId = useSession((state) => state.user?.id);
  return (record, field) => {
    const id = actorId(record, field);
    const name = actorDisplayName(record, field);
    if (!id && !name) return undefined;
    if (id && id === userId) return t('actor.you');
    return name ?? t('actor.colleague');
  };
}

/** True when the signed-in user did this step (so they cannot do the next one). */
export function useIsOwnStep(): (record: object, field: ActorField) => boolean {
  const userId = useSession((state) => state.user?.id);
  return (record, field) => Boolean(userId) && actorId(record, field) === userId;
}

/** A receiving account code (MAIN) in words; an account the web does not know keeps its code. */
export function useAccountLabel(): (code: string) => string {
  const { t } = useTranslation('portal');
  return (code) => t(`accounts.${code}` as 'accounts.MAIN', { defaultValue: code });
}
