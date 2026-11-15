/**
 * The terminal's offline state (B5): the cached snapshot, the queue of captures, whether it can
 * reach the server, and sending the queue when it can. The simulator can also be switched offline
 * by hand, to show the flow without pulling a cable.
 */
import { useCallback, useEffect, useEffectEvent, useRef, useState } from 'react';
import { ApiRequestError, NETWORK_ERROR } from '@/api/http-client';
import { decideOffline, normaliseUid, tagDigest, type OfflineDecision, type QueuedCapture, type Snapshot } from './offline';
import { dequeue, enqueue, loadQueue, loadSnapshot, saveSnapshot } from './offline-store';
import { fetchSnapshot, sendBatch, type DeviceIdentity, type SyncItemResult } from './pos-api';

const BATCH_SIZE = 100;
/** Refresh the snapshot this often while online. */
const SNAPSHOT_EVERY_MS = 10 * 60_000;
const FORCED_OFFLINE_KEY = 'zawed.pos.forcedOffline';

export interface SyncSummary {
  at: string;
  recorded: number;
  duplicates: number;
  rejected: number;
}

export function useOffline(device: DeviceIdentity) {
  const [snapshot, setSnapshot] = useState<Snapshot>();
  const [queue, setQueue] = useState<QueuedCapture[]>([]);
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
  const busy = useRef(false);

  const online = browserOnline && !forcedOffline && !unreachable;

  useEffect(() => {
    void loadSnapshot().then(setSnapshot);
    void loadQueue().then(setQueue);
    const up = () => {
      setBrowserOnline(true);
      setUnreachable(false);
    };
    const down = () => setBrowserOnline(false);
    window.addEventListener('online', up);
    window.addEventListener('offline', down);
    return () => {
      window.removeEventListener('online', up);
      window.removeEventListener('offline', down);
    };
  }, []);

  /** A request that never got an answer means the server is out of reach until proven otherwise. */
  const noteFailure = useCallback((error: unknown) => {
    if (error instanceof ApiRequestError && error.code === NETWORK_ERROR) setUnreachable(true);
  }, []);

  const refreshSnapshot = useCallback(async () => {
    try {
      const next = await fetchSnapshot(device);
      await saveSnapshot(next);
      setSnapshot(next);
      setUnreachable(false);
    } catch (error) {
      noteFailure(error);
    }
  }, [device, noteFailure]);

  /** Sends the queue in batches; the server's answer, whatever it is, takes an item off the queue. */
  const sync = useCallback(async () => {
    if (busy.current) return;
    busy.current = true;
    setSyncing(true);
    const summary: SyncSummary = { at: new Date().toISOString(), recorded: 0, duplicates: 0, rejected: 0 };
    try {
      let pending = await loadQueue();
      while (pending.length > 0) {
        const batch = pending.slice(0, BATCH_SIZE);
        const results: SyncItemResult[] = await sendBatch(
          device,
          batch.map(({ clientTxnId, tagUid, volumeMl, fuelType, deviceAt, agentId, receiptLocale }) => ({
            clientTxnId,
            tagUid,
            volumeMl,
            fuelType,
            deviceAt,
            agentId,
            receiptLocale,
          })),
          pending.length - batch.length,
        );
        for (const result of results) {
          if (result.result === 'RECORDED') summary.recorded++;
          else if (result.result === 'DUPLICATE') summary.duplicates++;
          else summary.rejected++;
        }
        await dequeue(results.map((result) => result.clientTxnId));
        pending = await loadQueue();
        setQueue(pending);
      }
      setUnreachable(false);
      if (summary.recorded + summary.duplicates + summary.rejected > 0) setLastSync(summary);
      await refreshSnapshot();
    } catch (error) {
      noteFailure(error);
    } finally {
      busy.current = false;
      setSyncing(false);
    }
  }, [device, noteFailure, refreshSnapshot]);

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

  /** Decides a capture against the snapshot and, if allowed, saves it in the queue before saying yes. */
  const captureOffline = useCallback(
    async (input: {
      clientTxnId: string;
      tagUid: string;
      volumeMl: number;
      agentId: string;
      receiptLocale: 'ar' | 'en';
    }): Promise<{ decision: OfflineDecision; capture?: QueuedCapture } | { noSnapshot: true }> => {
      if (!snapshot) return { noSnapshot: true };
      const uid = normaliseUid(input.tagUid);
      const digest = uid ? await tagDigest(snapshot.tagKey, uid) : '';
      const now = new Date();
      const decision = decideOffline(snapshot, queue, { digest, volumeMl: input.volumeMl, now });
      if (!decision.ok) return { decision };
      const capture: QueuedCapture = {
        clientTxnId: input.clientTxnId,
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
    [snapshot, queue],
  );

  return { online, forcedOffline, setForcedOffline: setForced, snapshot, queue, syncing, lastSync, sync, captureOffline, noteFailure };
}
