/**
 * Simulator only (DEV or VITE_POS_SIMULATOR_TOOLS=true): the UIDs this browser was given before,
 * one tap each, with the plate the terminal's snapshot matches. How a tester gets a UID:
 * docs/runbooks/qa-pos-tags.md. No API is called and no UID leaves this browser.
 */
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { FlaskConical, Trash2 } from 'lucide-react';
import { Icon, IconButton, Plate, StatusChip } from '@/design-system';
import { tagDigest, type Snapshot } from './offline';
import { forgetTag, loadRememberedTags, type RememberedTag } from './sim-tools';

/** The plate each remembered UID belongs to, by the snapshot's digests (undefined: not in the snapshot). */
function usePlates(tags: readonly RememberedTag[], snapshot: Snapshot | undefined) {
  const [plates, setPlates] = useState<Record<string, string | undefined>>({});
  useEffect(() => {
    if (!snapshot) return;
    let live = true;
    void Promise.all(
      tags.map(async (tag) => {
        const digest = await tagDigest(snapshot.tagKey, tag.uid);
        const vehicleId = snapshot.tags.find((candidate) => candidate.digest === digest)?.vehicleId;
        return [tag.uid, snapshot.vehicles.find((vehicle) => vehicle.id === vehicleId)?.plate] as const;
      }),
    ).then((pairs) => {
      if (live) setPlates(Object.fromEntries(pairs));
    });
    return () => {
      live = false;
    };
  }, [tags, snapshot]);
  return plates;
}

export function SimulatorTagPicker({ snapshot, onPick }: { snapshot: Snapshot | undefined; onPick: (uid: string) => void }) {
  const { t } = useTranslation('pos');
  const [tags, setTags] = useState(loadRememberedTags);
  const plates = usePlates(tags, snapshot);
  return (
    <section aria-label={t('live.sim.title')} className="w-full rounded-container border border-dashed border-border-strong bg-surface-sunken p-3 text-start" data-testid="pos-sim-tags">
      <div className="flex items-center justify-between gap-2">
        <p className="flex items-center gap-2 text-sm font-semibold">
          <Icon icon={FlaskConical} size={16} />
          {t('live.sim.title')}
        </p>
        <StatusChip tone="warning" label={t('live.sim.badge')} />
      </div>
      <p className="mt-1 text-xs text-muted">{t('live.sim.hint')}</p>
      {tags.length === 0 ? (
        <p className="mt-3 text-sm text-muted">{t('live.sim.empty')}</p>
      ) : (
        <ul className="mt-2 flex flex-col gap-1.5">
          {tags.map((tag) => (
            <li key={tag.uid} className="flex items-center gap-1">
              <button
                type="button"
                onClick={() => onPick(tag.uid)}
                className="flex min-h-12 min-w-0 flex-1 items-center justify-between gap-2 rounded-control border border-border bg-surface px-3 text-start transition-colors hover:bg-surface-hover"
              >
                <span dir="ltr" className="truncate font-mono text-sm">
                  {tag.uid}
                </span>
                {plates[tag.uid] ? <Plate value={plates[tag.uid]!} size="sm" /> : <span className="text-xs text-muted">{t('live.sim.notInSnapshot')}</span>}
              </button>
              <IconButton icon={Trash2} label={t('live.sim.forget', { uid: tag.uid })} onClick={() => setTags(forgetTag(tag.uid))} className="size-12" />
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
