/**
 * Simulator tools for demos and QA, never part of a real POS build.
 *
 * The web POS simulates the RFID reader: the agent types the UID a reader would return. Testers
 * cannot know a seeded tag's UID, and no API gives one out (a tenant never sees more than the last
 * four characters). So the tester gets a UID the documented way (docs/runbooks/qa-pos-tags.md:
 * Operations issues a tag with a UID the tester chose) and the simulator remembers the UIDs typed on
 * this browser, with a label, so the next read is one tap. Nothing here calls the API.
 *
 * Shown only in development (`import.meta.env.DEV`) or in a build made with
 * `VITE_POS_SIMULATOR_TOOLS=true`.
 */
export const SIMULATOR_TOOLS = import.meta.env.DEV || import.meta.env.VITE_POS_SIMULATOR_TOOLS === 'true';

export interface RememberedTag {
  uid: string;
  label?: string;
  lastUsedAt: string;
}

const KEY = 'zawed.pos.sim.tags';
const VERSION = 1;
const MAX_TAGS = 12;

export function loadRememberedTags(): RememberedTag[] {
  try {
    const raw = localStorage.getItem(KEY);
    const parsed = raw ? (JSON.parse(raw) as { v: number; tags: RememberedTag[] }) : undefined;
    return parsed?.v === VERSION && Array.isArray(parsed.tags) ? parsed.tags : [];
  } catch {
    return [];
  }
}

function save(tags: RememberedTag[]): RememberedTag[] {
  try {
    localStorage.setItem(KEY, JSON.stringify({ v: VERSION, tags }));
  } catch {
    // Storage blocked: nothing is remembered.
  }
  return tags;
}

/** Most recent first; a UID typed again moves to the top and keeps its label. */
export function rememberTag(uid: string, now: Date = new Date()): RememberedTag[] {
  const current = loadRememberedTags();
  const previous = current.find((tag) => tag.uid === uid);
  return save([{ uid, label: previous?.label, lastUsedAt: now.toISOString() }, ...current.filter((tag) => tag.uid !== uid)].slice(0, MAX_TAGS));
}

export function forgetTag(uid: string): RememberedTag[] {
  return save(loadRememberedTags().filter((tag) => tag.uid !== uid));
}
