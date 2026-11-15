/**
 * What the terminal keeps on itself to work offline, in IndexedDB so a closed tab or a reboot loses
 * nothing (B5: a persistent queue). Every capture names the device that made it.
 *
 * - `snapshot`: the last snapshot, with the offset between this clock and the server's.
 * - `queue`: captures not yet answered by the server.
 * - `synced`: captures the server recorded, kept in the decision base until a snapshot taken after
 *   their sync includes them, so a sync without a fresh snapshot cannot undo the provisional
 *   decrement (B5 review 5).
 * - `rejected`: captures the server could not record. Fuel was dispensed for each, so they are set
 *   aside for Operations, never deleted (B5 review 1).
 * - `attempts`: online fuelings whose answer never arrived, until the server says what it did
 *   (B5 review 6).
 */
import type { QueuedCapture, Snapshot } from './offline';

const DB_NAME = 'zawed-pos-offline';
const VERSION = 2;
const SNAPSHOT = 'snapshot';
const QUEUE = 'queue';
const SYNCED = 'synced';
const REJECTED = 'rejected';
const ATTEMPTS = 'attempts';

export interface StoredSnapshot {
  snapshot: Snapshot;
  /** Server time minus this clock, in milliseconds, measured when the snapshot arrived. */
  offsetMs: number;
}

export interface SyncedCapture extends QueuedCapture {
  /** Server time of the sync that recorded it. */
  syncedAt: string;
}

export interface RejectedCapture extends QueuedCapture {
  code: string;
  rejectedAt: string;
}

/** An online fueling sent but not answered. `told` is what the agent was shown meanwhile. */
export interface PendingAttempt {
  clientTxnId: string;
  deviceId: string;
  tagUid: string;
  volumeMl: number;
  startedAt: string;
  told: 'NOTHING' | 'REFUSED' | 'QUEUED';
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, VERSION);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains(SNAPSHOT)) db.createObjectStore(SNAPSHOT);
      for (const store of [QUEUE, SYNCED, REJECTED, ATTEMPTS]) {
        if (!db.objectStoreNames.contains(store)) db.createObjectStore(store, { keyPath: 'clientTxnId' });
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

/** One transaction over the given stores; resolves when it is committed (durable), not before. */
async function transact<T>(stores: string[], mode: IDBTransactionMode, work: (tx: IDBTransaction) => T): Promise<T> {
  const db = await openDb();
  try {
    return await new Promise<T>((resolve, reject) => {
      const tx = db.transaction(stores, mode);
      let result: T;
      try {
        result = work(tx);
      } catch (error) {
        tx.abort();
        reject(error);
        return;
      }
      tx.oncomplete = () => resolve(result);
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error ?? new Error('IndexedDB transaction aborted'));
    });
  } finally {
    db.close();
  }
}

function all<T>(store: string, deviceId: string): Promise<T[]> {
  return transact([store], 'readonly', (tx) => {
    const request = tx.objectStore(store).getAll() as IDBRequest<(T & { deviceId: string })[]>;
    return request;
  }).then((request) => request.result.filter((item) => item.deviceId === deviceId) as T[]);
}

const byCapture = (a: { deviceAt?: string; startedAt?: string }, b: { deviceAt?: string; startedAt?: string }) =>
  (a.deviceAt ?? a.startedAt ?? '').localeCompare(b.deviceAt ?? b.startedAt ?? '');

// ------------------------------------------------------------------------------------ snapshot

export async function loadSnapshot(): Promise<StoredSnapshot | undefined> {
  const request = await transact([SNAPSHOT], 'readonly', (tx) => tx.objectStore(SNAPSHOT).get('current') as IDBRequest<StoredSnapshot | undefined>);
  const stored = request.result;
  // Version 1 stored the bare snapshot.
  if (stored && !('snapshot' in stored)) return { snapshot: stored as unknown as Snapshot, offsetMs: 0 };
  return stored ?? undefined;
}

/** Saves the snapshot and, in the same transaction, forgets synced captures it now includes. */
export async function saveSnapshot(stored: StoredSnapshot): Promise<void> {
  const serverTime = stored.snapshot.serverTime;
  await transact([SNAPSHOT, SYNCED], 'readwrite', (tx) => {
    tx.objectStore(SNAPSHOT).put(stored, 'current');
    const synced = tx.objectStore(SYNCED);
    const cursor = synced.openCursor();
    cursor.onsuccess = () => {
      const item = cursor.result;
      if (!item) return;
      if ((item.value as SyncedCapture).syncedAt <= serverTime) item.delete();
      item.continue();
    };
  });
}

export async function clearSnapshot(): Promise<void> {
  await transact([SNAPSHOT], 'readwrite', (tx) => tx.objectStore(SNAPSHOT).delete('current'));
}

// --------------------------------------------------------------------------------------- queue

export async function loadQueue(deviceId: string): Promise<QueuedCapture[]> {
  return (await all<QueuedCapture>(QUEUE, deviceId)).sort(byCapture);
}

export async function loadSynced(deviceId: string): Promise<SyncedCapture[]> {
  return all<SyncedCapture>(SYNCED, deviceId);
}

export async function loadRejected(deviceId: string): Promise<RejectedCapture[]> {
  return (await all<RejectedCapture>(REJECTED, deviceId)).sort(byCapture);
}

/** Saves a capture; resolves only once it is durable, so the agent is told "done" after that. */
export async function enqueue(capture: QueuedCapture): Promise<void> {
  await transact([QUEUE], 'readwrite', (tx) => tx.objectStore(QUEUE).put(capture));
}

/**
 * Applies the server's answers in one transaction: recorded and duplicate move to `synced`,
 * rejected to `rejected`; anything else (retry, or no answer) stays queued.
 */
export async function applyAnswers(
  answers: readonly { clientTxnId: string; result: string; code?: string }[],
  serverNow: string,
): Promise<void> {
  await transact([QUEUE, SYNCED, REJECTED], 'readwrite', (tx) => {
    const queue = tx.objectStore(QUEUE);
    for (const answer of answers) {
      if (answer.result !== 'RECORDED' && answer.result !== 'DUPLICATE' && answer.result !== 'REJECTED') continue;
      const get = queue.get(answer.clientTxnId) as IDBRequest<QueuedCapture | undefined>;
      get.onsuccess = () => {
        const capture = get.result;
        if (!capture) return;
        if (answer.result === 'REJECTED') {
          tx.objectStore(REJECTED).put({ ...capture, code: answer.code ?? 'REJECTED', rejectedAt: serverNow } satisfies RejectedCapture);
        } else {
          tx.objectStore(SYNCED).put({ ...capture, syncedAt: serverNow } satisfies SyncedCapture);
        }
        queue.delete(answer.clientTxnId);
      };
    }
  });
}

// ------------------------------------------------------------------------------------ attempts

export async function saveAttempt(attempt: PendingAttempt): Promise<void> {
  await transact([ATTEMPTS], 'readwrite', (tx) => tx.objectStore(ATTEMPTS).put(attempt));
}

export async function dropAttempt(clientTxnId: string): Promise<void> {
  await transact([ATTEMPTS], 'readwrite', (tx) => tx.objectStore(ATTEMPTS).delete(clientTxnId));
}

export async function loadAttempts(deviceId: string): Promise<PendingAttempt[]> {
  return (await all<PendingAttempt>(ATTEMPTS, deviceId)).sort(byCapture);
}

// -------------------------------------------------------------------------------------- export

/** Everything this browser holds for any device, for Operations to recover by hand. */
export async function exportAll(): Promise<Record<string, unknown[]>> {
  const out: Record<string, unknown[]> = {};
  for (const store of [QUEUE, REJECTED, ATTEMPTS]) {
    out[store] = (await transact([store], 'readonly', (tx) => tx.objectStore(store).getAll())).result as unknown[];
  }
  return out;
}
