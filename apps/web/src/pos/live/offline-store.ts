/**
 * What the terminal keeps on itself to work offline: the last snapshot and the queue of captures
 * waiting to be sent. IndexedDB, so a closed tab or a reboot loses nothing (B5: a persistent queue).
 * Both belong to this device only and are cleared with it.
 */
import type { QueuedCapture, Snapshot } from './offline';

const DB_NAME = 'zawed-pos-offline';
const SNAPSHOT = 'snapshot';
const QUEUE = 'queue';

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, 1);
    request.onupgradeneeded = () => {
      request.result.createObjectStore(SNAPSHOT);
      request.result.createObjectStore(QUEUE, { keyPath: 'clientTxnId' });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function run<T>(store: string, mode: IDBTransactionMode, work: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await openDb();
  try {
    return await new Promise<T>((resolve, reject) => {
      const transaction = db.transaction(store, mode);
      const request = work(transaction.objectStore(store));
      // Resolve on commit, not on the request: a capture is only "saved" once it is durable.
      transaction.oncomplete = () => resolve(request.result);
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () => reject(transaction.error);
    });
  } finally {
    db.close();
  }
}

export async function loadSnapshot(): Promise<Snapshot | undefined> {
  return (await run<Snapshot | undefined>(SNAPSHOT, 'readonly', (store) => store.get('current'))) ?? undefined;
}

export async function saveSnapshot(snapshot: Snapshot): Promise<void> {
  await run(SNAPSHOT, 'readwrite', (store) => store.put(snapshot, 'current'));
}

/** The queue in capture order. */
export async function loadQueue(): Promise<QueuedCapture[]> {
  const items = await run<QueuedCapture[]>(QUEUE, 'readonly', (store) => store.getAll());
  return items.sort((a, b) => a.deviceAt.localeCompare(b.deviceAt));
}

export async function enqueue(capture: QueuedCapture): Promise<void> {
  await run(QUEUE, 'readwrite', (store) => store.add(capture));
}

/** Removes captures the server has answered for (recorded, duplicate or rejected). */
export async function dequeue(clientTxnIds: readonly string[]): Promise<void> {
  if (clientTxnIds.length === 0) return;
  const db = await openDb();
  try {
    await new Promise<void>((resolve, reject) => {
      const transaction = db.transaction(QUEUE, 'readwrite');
      const store = transaction.objectStore(QUEUE);
      for (const id of clientTxnIds) store.delete(id);
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error);
    });
  } finally {
    db.close();
  }
}

export async function clearSnapshot(): Promise<void> {
  await run(SNAPSHOT, 'readwrite', (store) => store.delete('current'));
}

/** Forget everything, the queue included. Only for a deliberate reset of a terminal. */
export async function clearOffline(): Promise<void> {
  await new Promise<void>((resolve) => {
    const request = indexedDB.deleteDatabase(DB_NAME);
    request.onsuccess = () => resolve();
    request.onerror = () => resolve();
    request.onblocked = () => resolve();
  });
}
