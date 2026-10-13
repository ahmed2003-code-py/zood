/**
 * Shared demo state: the in-browser stand-in for the backend AND for the physical world the
 * simulator needs (tag memory, the POS's offline queue). Every perspective reads the same state,
 * so a POS transaction appears everywhere at once. Persisted to localStorage and synchronized
 * across open windows of the demo (storage events → rehydrate).
 */
import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';
import type { Dataset } from '@/domain/dataset';
import { recordFuelTransaction, type FuelOutcome, type FuelRequest } from '@/domain/fueling';
import { amountForVolume, litersToMl } from '@/domain/money';
import { provisionalSnapshot, validateOffline, type QueuedFuel } from '@/domain/pos';
import type { DeclineReason, Id, IsoDateTime, TagStatus } from '@/domain/types';
import { STORAGE_KEYS } from '@/lib/storage';
import { createSeedDataset, DEMO_IDS } from './seed';

export type NetworkMode = 'online' | 'offline';
export type PrinterMode = 'ready' | 'failed';

/** Conditions the presenter can simulate for the POS. */
export interface Simulation {
  network: NetworkMode;
  printer: PrinterMode;
  /** The tag the simulated RFID reader returns on the next scan. */
  selectedTagId: Id;
}

export interface SyncState {
  status: 'idle' | 'syncing';
  lastSyncedAt?: IsoDateTime;
  lastSyncedCount?: number;
}

export type OfflineCaptureResult = { ok: true; item: QueuedFuel } | { ok: false; reason: DeclineReason };

interface DemoState {
  data: Dataset;
  simulation: Simulation;
  /** Cooldown records written on the physical tags (any POS reads them, online or offline). */
  tagMemory: Record<Id, IsoDateTime>;
  /** The demo POS's local queue of offline captures waiting for synchronization. */
  posQueue: QueuedFuel[];
  sync: SyncState;
  recordFuel: (request: FuelRequest) => FuelOutcome;
  captureOffline: (request: FuelRequest) => OfflineCaptureResult;
  syncQueue: () => void;
  setNetwork: (network: NetworkMode) => void;
  setPrinter: (printer: PrinterMode) => void;
  selectTag: (tagId: Id) => void;
  setTagStatus: (tagId: Id, status: TagStatus) => void;
  resolveAlert: (alertId: Id) => void;
  triggerTankAnomaly: () => FuelOutcome | undefined;
  reset: () => void;
}

const SCHEMA_VERSION = 3;
const SYNC_DELAY_MS = 1600;

function initialState() {
  return {
    data: createSeedDataset(),
    simulation: { network: 'online', printer: 'ready', selectedTagId: `tag-${DEMO_IDS.vehicleCorolla}` } as Simulation,
    tagMemory: {} as Record<Id, IsoDateTime>,
    posQueue: [] as QueuedFuel[],
    sync: { status: 'idle' } as SyncState,
  };
}

let syncTimer: ReturnType<typeof setTimeout> | undefined;

export const useDemoStore = create<DemoState>()(
  persist(
    (set, get) => ({
      ...initialState(),

      recordFuel: (request) => {
        const result = recordFuelTransaction(get().data, request, new Date());
        if (result.outcome.replayed) return result.outcome;
        const txn = result.outcome.transaction;
        const company = result.data.companies[txn.companyId];
        set((state) => ({
          data: result.data,
          // The POS writes the cooldown record to the tag right after a successful fueling (PRD §7.3).
          tagMemory:
            txn.status === 'completed' && company
              ? { ...state.tagMemory, [txn.tagId]: new Date(Date.now() + company.cooldownMinutes * 60_000).toISOString() }
              : state.tagMemory,
        }));
        return result.outcome;
      },

      captureOffline: (request) => {
        const { data, posQueue, tagMemory } = get();
        if (posQueue.some((item) => item.request.clientTxnId === request.clientTxnId)) {
          const item = posQueue.find((queued) => queued.request.clientTxnId === request.clientTxnId);
          if (item) return { ok: true, item };
        }
        const tag = data.tags[request.tagId];
        const vehicle = tag ? data.vehicles[tag.vehicleId] : undefined;
        const station = data.stations[request.stationId];
        const unitPricePiastres = vehicle ? (station?.prices[vehicle.fuelTypeId] ?? 0) : 0;
        const amountPiastres = amountForVolume(request.volumeMl, unitPricePiastres);
        // Validate against the cached snapshot with this device's pending captures applied.
        const reason = validateOffline(provisionalSnapshot(data, posQueue), request, amountPiastres, new Date(request.deviceAt));
        if (reason) return { ok: false, reason };
        const company = vehicle ? data.companies[vehicle.companyId] : undefined;
        const item: QueuedFuel = { request, amountPiastres, unitPricePiastres, capturedAt: request.deviceAt };
        set({
          posQueue: [...posQueue, item],
          tagMemory: company
            ? { ...tagMemory, [request.tagId]: new Date(new Date(request.deviceAt).getTime() + company.cooldownMinutes * 60_000).toISOString() }
            : tagMemory,
        });
        return { ok: true, item };
      },

      syncQueue: () => {
        const { posQueue, sync, simulation } = get();
        if (posQueue.length === 0 || sync.status === 'syncing' || simulation.network !== 'online') return;
        set({ sync: { ...sync, status: 'syncing' } });
        clearTimeout(syncTimer);
        syncTimer = setTimeout(() => {
          const state = get();
          if (state.simulation.network !== 'online') {
            set({ sync: { ...state.sync, status: 'idle' } });
            return;
          }
          // Each queued capture is pushed with its unique id; the server deduplicates and commits.
          let data = state.data;
          const now = new Date();
          for (const item of state.posQueue) {
            data = recordFuelTransaction(data, { ...item.request, origin: 'offline' }, now).data;
          }
          const devices = { ...data.devices };
          const device = devices[DEMO_IDS.device];
          if (device) devices[DEMO_IDS.device] = { ...device, lastSyncAt: now.toISOString() };
          set({
            data: { ...data, devices },
            posQueue: [],
            sync: { status: 'idle', lastSyncedAt: now.toISOString(), lastSyncedCount: state.posQueue.length },
          });
        }, SYNC_DELAY_MS);
      },

      setNetwork: (network) => {
        set((state) => ({ simulation: { ...state.simulation, network } }));
        // Background sync starts automatically when connectivity returns (PRD §7.5).
        if (network === 'online') get().syncQueue();
      },
      setPrinter: (printer) => set((state) => ({ simulation: { ...state.simulation, printer } })),
      selectTag: (selectedTagId) => set((state) => ({ simulation: { ...state.simulation, selectedTagId } })),

      setTagStatus: (tagId, status) =>
        set((state) => {
          const tag = state.data.tags[tagId];
          if (!tag) return state;
          return { data: { ...state.data, tags: { ...state.data.tags, [tagId]: { ...tag, status } } } };
        }),

      resolveAlert: (alertId) =>
        set((state) => ({
          data: {
            ...state.data,
            alerts: state.data.alerts.map((alert) => (alert.id === alertId ? { ...alert, status: 'resolved' } : alert)),
          },
        })),

      /** Presenter: a completed 62 L fueling for a 50 L tank — valid, but flagged to Operations (PRD §10). */
      triggerTankAnomaly: () => {
        const now = new Date();
        const tagId = `tag-${DEMO_IDS.vehicleAnomaly}`;
        const state = get();
        const tag = state.data.tags[tagId];
        if (!tag) return undefined;
        // Demo tooling: allow re-triggering by clearing this tag's cooldown first.
        const data = { ...state.data, tags: { ...state.data.tags, [tagId]: { ...tag, cooldownUntil: undefined, status: 'active' as const } } };
        const result = recordFuelTransaction(
          data,
          {
            clientTxnId: crypto.randomUUID(),
            tagId,
            stationId: DEMO_IDS.station,
            deviceId: 'dev-ns-02',
            agentId: 'ag-20112',
            volumeMl: litersToMl(62),
            origin: 'online',
            deviceAt: now.toISOString(),
            receiptLocale: 'ar',
          },
          now,
        );
        set({ data: result.data });
        return result.outcome;
      },

      reset: () => {
        clearTimeout(syncTimer);
        set(initialState());
      },
    }),
    {
      name: STORAGE_KEYS.demoState,
      version: SCHEMA_VERSION,
      storage: createJSONStorage(() => localStorage),
      partialize: (state) => ({
        data: state.data,
        simulation: state.simulation,
        tagMemory: state.tagMemory,
        posQueue: state.posQueue,
        sync: state.sync,
      }),
      // A stored dataset from an older schema is replaced by a fresh seed.
      migrate: () => initialState(),
    },
  ),
);

// Other demo windows (e.g. the POS next to the company portal) pick up changes immediately.
if (typeof window !== 'undefined') {
  window.addEventListener('storage', (event) => {
    if (event.key === STORAGE_KEYS.demoState) void useDemoStore.persist.rehydrate();
  });
}
