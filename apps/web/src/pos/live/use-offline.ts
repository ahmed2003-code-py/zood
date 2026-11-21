/**
 * The terminal's offline state (B5): the cached snapshot and its clock offset, this device's queue,
 * what the server refused, online attempts whose answer never came, whether the server can be
 * reached, and sending the queue when it can. It lives at the device level, so the queue is sent
 * even when no agent is signed in (B5 contract §3: after the shift). The simulator can also be
 * switched offline by hand.
 */
import { useCallback, useEffect, useEffectEvent, useRef, useState } from 'react';
import { ApiRequestError, NETWORK_ERROR } from '@/api/http-client';
import { decideOffline, normaliseUid, tagDigest, type OfflineDecision, type QueuedCapture } from './offline';
import {
  applyAnswers,
  dropAttempt,
  enqueue,
  exportAll,
  loadAttempts,
  loadQueue,
  loadRejected,
  loadSnapshot,
  loadSynced,
  saveAttempt,
  saveSnapshot,
  type PendingAttempt,
  type StoredSnapshot,
  type SyncedCapture,
} from './offline-store';
import { fetchSnapshot, resolveAttempt, sendBatch, type AgentSession, type DeviceIdentity } from './pos-api';

const BATCH_SIZE = 100;
/** Refresh the snapshot this often while online. */
const SNAPSHOT_EVERY_MS = 10 * 60_000;
const FORCED_OFFLINE_KEY = 'zawed.pos.forcedOffline';

export interface SyncSummary {
  at: string;
  recorded: number;
  duplicates: number;
  rejected: number;
  retried: number;
}

export function useOffline(device: DeviceIdentity, session: AgentSession | undefined) {
  const [stored, setStored] = useState<StoredSnapshot>();
  const [queue, setQueue] = useState<QueuedCapture[]>([]);
  const [synced, setSynced] = useState<SyncedCapture[]>([]);
  const [rejectedCount, setRejectedCount] = useState(0);
  const [resolvedCount, setResolvedCount] = useState(0);
  const [browserOnline, setBrowserOnline] = useState(() => navigator.onLine);
  const [forcedOffline, setForcedOffline] = useState(() => {
    try {
      return sessionStorage.getItem(FORCED_OFFLINE_KEY) === '1';
    } catch {
      return false;
    }
  });
  const [unreachable, setUnreachable] = useState(false);
  const [syncing, setSyncing] = useState(false);
  const [lastSync, setLastSync] = useState<SyncSummary>();
  const [lastError, setLastError] = useState<string>();
  const busy = useRef(false);

  const online = browserOnline && !forcedOffline && !unreachable;
  const offsetMs = stored?.offsetMs ?? 0;
  /** Now, on the server's clock as of the last snapshot. */
  const serverNow = useCallback(() => new Date(Date.now() + offsetMs), [offsetMs]);

  const reload = useCallback(async () => {
    const [nextQueue, nextSynced, rejected] = await Promise.all([
      loadQueue(device.deviceId),
      loadSynced(device.deviceId),
      loadRejected(device.deviceId),
    ]);
    setQueue(nextQueue);
    setSynced(nextSynced);
    setRejectedCount(rejected.length);
  }, [device.deviceId]);

  useEffect(() => {
    // Loaded in a task, not in the effect itself, so the state it sets does not cascade into this render.
    const initial = setTimeout(() => {
      void loadSnapshot().then(setStored);
      void reload();
    }, 0);
    const up = () => {
      setBrowserOnline(true);
      setUnreachable(false);
    };
    const down = () => setBrowserOnline(false);
    window.addEventListener('online', up);
    window.addEventListener('offline', down);
    return () => {
      clearTimeout(initial);
      window.removeEventListener('online', up);
      window.removeEventListener('offline', down);
    };
  }, [reload]);

  /** No answer at all means the server is out of reach; any other refusal is shown, not swallowed. */
  const noteFailure = useCallback((error: unknown) => {
    if (error instanceof ApiRequestError && error.code === NETWORK_ERROR) setUnreachable(true);
    else setLastError(error instanceof ApiRequestError ? error.code : 'UNKNOWN');
  }, []);

  /** Fetches a new snapshot; answers it, or undefined when the server could not be reached. */
  const refreshSnapshot = useCallback(async (): Promise<StoredSnapshot | undefined> => {
    try {
      const before = Date.now();
      const snapshot = await fetchSnapshot(device);
      const after = Date.now();
      const next: StoredSnapshot = { snapshot, offsetMs: new Date(snapshot.serverTime).getTime() - (before + after) / 2 };
      await saveSnapshot(next);
      setStored(next);
      setUnreachable(false);
      await reload();
      return next;
    } catch (error) {
      noteFailure(error);
      return undefined;
    }
  }, [device, noteFailure, reload]);

  /** Asks the server what became of online attempts whose answer never arrived (B5 review 6). */
  const resolveAttempts = useCallback(async () => {
    if (!session) return;
    for (const attempt of await loadAttempts(device.deviceId)) {
      const outcome = await resolveAttempt(device, session, attempt.clientTxnId);
      if (outcome === 'UNREACHABLE') return;
      if (outcome === 'RECORDED') setResolvedCount((count) => count + 1);
      await dropAttempt(attempt.clientTxnId);
    }
  }, [device, session]);

  /** Sends this device's queue in batches and files each answer; retries stay queued. */
  const sync = useCallback(async () => {
    if (busy.current) return;
    busy.current = true;
    setSyncing(true);
    const summary: SyncSummary = { at: new Date().toISOString(), recorded: 0, duplicates: 0, rejected: 0, retried: 0 };
    try {
      await resolveAttempts();
      let pending = await loadQueue(device.deviceId);
      while (pending.length > 0) {
        const batch = pending.slice(0, BATCH_SIZE);
        const ids = new Set(batch.map((item) => item.clientTxnId));
        const results = await sendBatch(
          device,
          batch.map(({ clientTxnId, tagUid, volumeMl, fuelType, deviceAt, agentId, receiptLocale, unitPricePiastres }) => ({
            clientTxnId,
            tagUid,
            volumeMl,
            fuelType,
            deviceAt,
            agentId,
            receiptLocale,
            unitPricePiastres,
          })),
          pending.length - batch.length,
        );
        const answered = results.filter((result) => ids.has(result.clientTxnId));
        for (const result of answered) {
          if (result.result === 'RECORDED') summary.recorded++;
          else if (result.result === 'DUPLICATE') summary.duplicates++;
          else if (result.result === 'REJECTED') summary.rejected++;
          else summary.retried++;
        }
        await applyAnswers(answered, new Date(Date.now() + offsetMs).toISOString());
        const next = await loadQueue(device.deviceId);
        // Nothing settled (all retried): stop, try again later instead of spinning.
        if (next.length >= pending.length) break;
        pending = next;
      }
      setUnreachable(false);
      setLastError(undefined);
      if (summary.recorded + summary.duplicates + summary.rejected + summary.retried > 0) setLastSync(summary);
      await reload();
      await refreshSnapshot();
    } catch (error) {
      noteFailure(error);
      await reload();
    } finally {
      busy.current = false;
      setSyncing(false);
    }
  }, [device, offsetMs, noteFailure, refreshSnapshot, reload, resolveAttempts]);

  // Online: send what is waiting, and keep the snapshot fresh. The first run is a task, not part of
  // the effect, so the state it sets does not cascade into this render.
  const syncFromEffect = useEffectEvent(() => void sync());
  useEffect(() => {
    if (!online) return;
    const first = setTimeout(syncFromEffect, 0);
    const timer = setInterval(syncFromEffect, SNAPSHOT_EVERY_MS);
    return () => {
      clearTimeout(first);
      clearInterval(timer);
    };
  }, [online]);

  // Out of reach: try again every 30 seconds.
  useEffect(() => {
    if (!unreachable || forcedOffline || !browserOnline) return;
    const timer = setInterval(() => void refreshSnapshot(), 30_000);
    return () => clearInterval(timer);
  }, [unreachable, forcedOffline, browserOnline, refreshSnapshot]);

  const setForced = useCallback((value: boolean) => {
    setForcedOffline(value);
    try {
      sessionStorage.setItem(FORCED_OFFLINE_KEY, value ? '1' : '0');
    } catch {
      // Not remembered across reloads.
    }
  }, []);

  /** Remembers an online attempt before it is sent, so a lost answer or a reload loses nothing. */
  const beginAttempt = useCallback(
    (attempt: Omit<PendingAttempt, 'deviceId' | 'startedAt' | 'told'>) =>
      saveAttempt({ ...attempt, deviceId: device.deviceId, startedAt: new Date(Date.now() + offsetMs).toISOString(), told: 'NOTHING' }),
    [device.deviceId, offsetMs],
  );

  /**
   * After a lost answer: a capture queued offline covers the attempt (the sync settles it), so it is
   * dropped; one the terminal refused stays, marked, until the server says what it did.
   */
  const settleAttempt = useCallback(async (clientTxnId: string, outcome: 'QUEUED' | 'REFUSED' | 'ANSWERED') => {
    if (outcome === 'REFUSED') {
      const attempt = (await loadAttempts(device.deviceId)).find((item) => item.clientTxnId === clientTxnId);
      if (attempt) await saveAttempt({ ...attempt, told: 'REFUSED' });
    } else {
      await dropAttempt(clientTxnId);
    }
  }, [device.deviceId]);

  /**
   * Decides a capture against the snapshot plus this device's queued and not-yet-included synced
   * captures and, if allowed, saves it durably before saying yes.
   */
  const captureOffline = useCallback(
    async (input: {
      clientTxnId: string;
      tagUid: string;
      volumeMl: number;
      agentId: string;
      receiptLocale: 'ar' | 'en';
    }): Promise<{ decision: OfflineDecision; capture?: QueuedCapture } | { noSnapshot: true }> => {
      if (!stored) return { noSnapshot: true };
      const snapshot = stored.snapshot;
      const uid = normaliseUid(input.tagUid);
      const digest = uid ? await tagDigest(snapshot.tagKey, uid) : '';
      const now = serverNow();
      const decision = decideOffline(snapshot, [...queue, ...synced], { digest, volumeMl: input.volumeMl, now });
      if (!decision.ok) return { decision };
      const capture: QueuedCapture = {
        clientTxnId: input.clientTxnId,
        deviceId: device.deviceId,
        tagUid: uid!,
        digest,
        vehicleId: decision.vehicleId,
        companyId: decision.companyId,
        plate: decision.plate,
        fuelType: decision.fuelType,
        volumeMl: input.volumeMl,
        unitPricePiastres: decision.unitPricePiastres,
        amountPiastres: decision.amountPiastres,
        billable: decision.billable,
        deviceAt: now.toISOString(),
        agentId: input.agentId,
        receiptLocale: input.receiptLocale,
      };
      await enqueue(capture);
      setQueue((current) => [...current, capture]);
      return { decision, capture };
    },
    [stored, queue, synced, serverNow, device.deviceId],
  );

  /** Everything this browser holds, as a file for Operations (queue, refused captures, attempts). */
  const exportQueue = useCallback(async () => {
    const data = await exportAll();
    const blob = new Blob([JSON.stringify({ exportedAt: new Date().toISOString(), device, ...data }, null, 2)], {
      type: 'application/json',
    });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `zawed-pos-${device.deviceCode}-${Date.now()}.json`;
    link.click();
    URL.revokeObjectURL(url);
  }, [device]);

  return {
    online,
    forcedOffline,
    setForcedOffline: setForced,
    snapshot: stored?.snapshot,
    serverNow,
    queue,
    /** Sent captures the snapshot may not include yet: they still count against limits and cooldowns. */
    synced,
    refreshSnapshot,
    rejectedCount,
    resolvedCount,
    syncing,
    lastSync,
    lastError,
    sync,
    captureOffline,
    beginAttempt,
    settleAttempt,
    noteFailure,
    exportQueue,
  };
}

export type OfflineState = ReturnType<typeof useOffline>;
